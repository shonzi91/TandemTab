using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FinApp.Server.Tests;

/// <summary>
/// The rest of the suite runs with the passthrough cipher (no KMS in tests), so the encrypted path — the one
/// production actually uses — was only ever covered by unit tests on the cipher itself. These drive the real
/// endpoints with a real envelope cipher wired in, which is where the compression change could break things:
/// production rows are all v1 envelopes written before compression existed, and they are never migrated.
/// </summary>
public sealed class EncryptingServerFactory : FinAppServerFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISnapshotCipher>();
            services.AddSingleton<ISnapshotCipher, LocalEnvelopeCipher>();
        });
    }
}

public class SnapshotEncryptionEndToEndTests : IClassFixture<EncryptingServerFactory>
{
    private readonly EncryptingServerFactory _factory;

    public SnapshotEncryptionEndToEndTests(EncryptingServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>A payload shaped like a real snapshot: many similar records, which is what compresses.</summary>
    private static string BigPayload(string marker) =>
        "[" + string.Join(",", Enumerable.Range(0, 800).Select(i =>
            $$"""{"Id":"{{Guid.Empty}}","Kind":"Expense","Amount":{{i}}.50,"Note":"{{marker}} groceries"}""")) + "]";

    private string StoredPayload(Guid accountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
        return db.AccountSnapshots.Single(r => r.AccountId == accountId).Payload;
    }

    [Fact]
    public async Task Save_stores_a_compressed_envelope_and_get_returns_the_original()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("enc_rw");
        var account = await CreateAccount(client, "Encrypted");
        var payload = BigPayload("weekly");

        await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot", new SaveAccountRequest(payload, 0));

        var stored = StoredPayload(account.Id);
        Assert.StartsWith("ENC2:", stored);
        Assert.DoesNotContain("groceries", stored);
        // The whole point of the change: the stored column is a fraction of the payload, despite base64's ~33%.
        Assert.True(stored.Length < payload.Length / 5,
            $"expected a compressed row, got {stored.Length}B stored for {payload.Length}B payload");

        var got = await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot");
        Assert.Equal(payload, got!.Payload);
    }

    [Fact]
    public async Task A_row_written_before_compression_still_reads_and_upgrades_on_its_next_save()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("enc_legacy");
        var account = await CreateAccount(client, "Legacy");
        const string original = """{"Id":"old","note":"written before compression"}""";

        // Seed the row exactly as production holds it today: a v1 envelope, no gzip inside.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
            db.AccountSnapshots.Add(new AccountSnapshotRow
            {
                AccountId = account.Id,
                Payload = LocalEnvelopeCipher.ProtectUncompressed(original),
                Version = 7,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var got = await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot");
        Assert.Equal(original, got!.Payload);
        Assert.Equal(7, got.Version);

        // Saving over it rewrites in the new format — no migration needed, rows upgrade as they're touched.
        await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot", new SaveAccountRequest("""{"now":"v2"}""", 7));
        Assert.StartsWith("ENC2:", StoredPayload(account.Id));
        var after = await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot");
        Assert.Equal("""{"now":"v2"}""", after!.Payload);
    }

    [Fact]
    public async Task The_legacy_encryption_pass_leaves_both_envelope_versions_alone()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("enc_migrate");
        var account = await CreateAccount(client, "Migrate");
        await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot", new SaveAccountRequest("""{"a":1}""", 0));
        var before = StoredPayload(account.Id);

        // Re-Protecting an already-encrypted row would double-wrap it into something no reader can open.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SnapshotService>().EncryptLegacyRowsAsync();

        Assert.Equal(before, StoredPayload(account.Id));
        var got = await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot");
        Assert.Equal("""{"a":1}""", got!.Payload);
    }
}
