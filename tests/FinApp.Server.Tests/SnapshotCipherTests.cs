using FinApp.Server.Accounts;
using Xunit;

namespace FinApp.Server.Tests;

public class SnapshotCipherTests
{
    [Fact]
    public async Task Round_trips_a_payload_and_hides_the_plaintext()
    {
        var cipher = new LocalEnvelopeCipher();
        const string payload = """{"Id":"abc","secret":"€1,234.56 groceries"}""";

        var stored = await cipher.ProtectAsync(payload);

        Assert.True(cipher.IsEncrypted(stored));
        Assert.StartsWith("ENC2:", stored);
        Assert.DoesNotContain("groceries", stored);            // plaintext must not leak into the stored form
        Assert.Equal(payload, await cipher.UnprotectAsync(stored));
    }

    [Fact]
    public async Task Reads_a_v1_envelope_written_before_compression()
    {
        var cipher = new LocalEnvelopeCipher();
        const string payload = """{"Id":"abc","secret":"€1,234.56 groceries"}""";

        // Rows written before ENC2 hold raw UTF-8 inside the ciphertext. They are never rewritten by a migration —
        // they upgrade on their next save — so reading them has to keep working indefinitely.
        var v1 = LocalEnvelopeCipher.ProtectUncompressed(payload);

        Assert.StartsWith("ENC1:", v1);
        Assert.True(cipher.IsEncrypted(v1));
        Assert.Equal(payload, await cipher.UnprotectAsync(v1));
    }

    [Fact]
    public async Task Compresses_the_payload_so_the_stored_row_is_far_smaller()
    {
        var cipher = new LocalEnvelopeCipher();
        // Shaped like a real snapshot: many similar records, which is why gzip pays here at all.
        var payload = "[" + string.Join(",", Enumerable.Range(0, 500).Select(i =>
            $$"""{"Id":"{{Guid.Empty}}","Kind":"Expense","Amount":{{i}}.50,"Note":"weekly groceries"}""")) + "]";

        var stored = await cipher.ProtectAsync(payload);

        // Base64 alone inflates by ~33%, so an uncompressed envelope would be LARGER than the payload.
        Assert.True(stored.Length < payload.Length / 5,
            $"expected heavy compression, got {stored.Length}B stored for {payload.Length}B payload");
        Assert.Equal(payload, await cipher.UnprotectAsync(stored));
    }

    [Fact]
    public async Task Each_write_uses_a_fresh_key_so_ciphertexts_differ()
    {
        var cipher = new LocalEnvelopeCipher();
        var a = await cipher.ProtectAsync("same input");
        var b = await cipher.ProtectAsync("same input");
        Assert.NotEqual(a, b);
        Assert.Equal("same input", await cipher.UnprotectAsync(a));
        Assert.Equal("same input", await cipher.UnprotectAsync(b));
    }

    [Fact]
    public async Task Legacy_plaintext_is_passed_through_on_read()
    {
        var cipher = new LocalEnvelopeCipher();
        const string legacy = """{"Id":"old","plain":true}""";   // pre-encryption row, no ENC1: prefix
        Assert.False(cipher.IsEncrypted(legacy));
        Assert.Equal(legacy, await cipher.UnprotectAsync(legacy));
    }

    [Fact]
    public async Task With_compression_off_it_writes_v1_but_still_reads_v2()
    {
        // Phase 1 of the rollout: this build can read the new format, but doesn't produce it yet, so rolling back
        // to the previous build stays safe. The flag is also the undo if phase 2 goes wrong.
        var writer = new LocalEnvelopeCipher(compressWrites: false);
        const string payload = """{"Id":"abc","secret":"€1,234.56 groceries"}""";

        var stored = await writer.ProtectAsync(payload);
        Assert.StartsWith("ENC1:", stored);
        Assert.DoesNotContain("groceries", stored);
        Assert.Equal(payload, await writer.UnprotectAsync(stored));

        // Reading is unconditional — a row written while the flag was on must survive turning it back off.
        var v2 = await new LocalEnvelopeCipher(compressWrites: true).ProtectAsync(payload);
        Assert.StartsWith("ENC2:", v2);
        Assert.Equal(payload, await writer.UnprotectAsync(v2));
    }

    [Fact]
    public async Task Passthrough_cipher_stores_plaintext_and_does_not_claim_to_encrypt()
    {
        var cipher = new PassthroughSnapshotCipher();
        Assert.False(cipher.Encrypts);
        Assert.Equal("hello", await cipher.ProtectAsync("hello"));
        Assert.Equal("hello", await cipher.UnprotectAsync("hello"));
    }
}
