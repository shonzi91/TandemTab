using System.Security.Cryptography;
using FinApp.Server.Accounts;
using Xunit;

namespace FinApp.Server.Tests;

public class SnapshotCipherTests
{
    /// <summary>Envelope cipher that "wraps" the data key with a fixed local AES key instead of KMS — lets us test
    /// the format + round-trip deterministically without cloud credentials.</summary>
    private sealed class LocalEnvelopeCipher : EnvelopeSnapshotCipher
    {
        private static readonly byte[] Kek = SHA256.HashData("test-kek"u8.ToArray());

        protected override Task<byte[]> WrapAsync(byte[] dek, CancellationToken ct)
        {
            var nonce = new byte[12];
            var ctBytes = new byte[dek.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Kek, 16);
            aes.Encrypt(nonce, dek, ctBytes, tag);
            return Task.FromResult(tag.Concat(ctBytes).ToArray());
        }

        protected override Task<byte[]> UnwrapAsync(byte[] wrapped, CancellationToken ct)
        {
            var tag = wrapped[..16];
            var ctBytes = wrapped[16..];
            var dek = new byte[ctBytes.Length];
            using var aes = new AesGcm(Kek, 16);
            aes.Decrypt(new byte[12], ctBytes, tag, dek);
            return Task.FromResult(dek);
        }
    }

    [Fact]
    public async Task Round_trips_a_payload_and_hides_the_plaintext()
    {
        var cipher = new LocalEnvelopeCipher();
        const string payload = """{"Id":"abc","secret":"€1,234.56 groceries"}""";

        var stored = await cipher.ProtectAsync(payload);

        Assert.True(cipher.IsEncrypted(stored));
        Assert.StartsWith("ENC1:", stored);
        Assert.DoesNotContain("groceries", stored);            // plaintext must not leak into the stored form
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
    public async Task Passthrough_cipher_stores_plaintext_and_does_not_claim_to_encrypt()
    {
        var cipher = new PassthroughSnapshotCipher();
        Assert.False(cipher.Encrypts);
        Assert.Equal("hello", await cipher.ProtectAsync("hello"));
        Assert.Equal("hello", await cipher.UnprotectAsync("hello"));
    }
}
