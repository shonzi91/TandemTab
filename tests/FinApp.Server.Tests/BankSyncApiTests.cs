using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// Bank sync is inert until GoCardless credentials are configured (like external sign-in). These tests run
/// against the default, unconfigured server, so they assert the feature reports itself off and that calls
/// needing the provider fail cleanly rather than crashing — and that the staging tables exist and enforce
/// access scoping regardless.
/// </summary>
public class BankSyncApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public BankSyncApiTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid AccountId)> AccountAsync(string user)
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync(user);
        // Bank features now require a verified email; verify so these tests exercise real bank behavior, not the gate.
        await _factory.MarkEmailVerifiedAsync(auth.UserId, auth.Email);
        var created = await (await client.PostAsJsonAsync("/accounts",
            new CreateAccountRequest("Main", "GBP"))).Content.ReadFromJsonAsync<AccountSummaryDto>();
        return (client, created!.Id);
    }

    [Fact]
    public async Task Status_reports_disabled_when_provider_unconfigured()
    {
        var (client, accountId) = await AccountAsync("bank1");

        var status = await client.GetFromJsonAsync<BankSyncStatusDto>($"/accounts/{accountId}/bank/status");

        Assert.NotNull(status);
        Assert.False(status!.Enabled);
        Assert.False(status.Connected);
        Assert.Null(status.InstitutionName);
    }

    [Fact]
    public async Task Pending_is_empty_before_any_sync()
    {
        var (client, accountId) = await AccountAsync("bank2");

        var pending = await client.GetFromJsonAsync<List<PendingBankTransactionDto>>($"/accounts/{accountId}/bank/pending");

        Assert.NotNull(pending);
        Assert.Empty(pending!);
    }

    [Fact]
    public async Task Sync_without_a_linked_bank_is_rejected()
    {
        var (client, accountId) = await AccountAsync("bank3");

        var resp = await client.PostAsync($"/accounts/{accountId}/bank/sync", null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bank_endpoints_are_scoped_to_contributors()
    {
        var (_, accountId) = await AccountAsync("bankOwner");
        var (stranger, sauth) = await _factory.RegisterAndAuthAsync("bankStranger");
        await _factory.MarkEmailVerifiedAsync(sauth.UserId, sauth.Email);   // past the email gate, so we test scoping (404), not 403

        var resp = await stranger.GetAsync($"/accounts/{accountId}/bank/status");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Bank_endpoints_require_a_verified_email()
    {
        // A freshly registered user is unverified; bank features expose real financial data, so they're blocked.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bankUnverified");
        var accountId = (await (await client.PostAsJsonAsync("/accounts",
            new CreateAccountRequest("Main", "GBP"))).Content.ReadFromJsonAsync<AccountSummaryDto>())!.Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/accounts/{accountId}/bank/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/accounts/{accountId}/bank/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/accounts/{accountId}/bank/sync", null)).StatusCode);

        // Once verified, the same call proceeds (here: reports the provider is unconfigured).
        await _factory.MarkEmailVerifiedAsync(auth.UserId, auth.Email);
        var status = await client.GetFromJsonAsync<BankSyncStatusDto>($"/accounts/{accountId}/bank/status");
        Assert.False(status!.Enabled);
    }

    [Fact]
    public async Task Merchant_mappings_persist_and_can_be_removed()
    {
        var (client, accountId) = await AccountAsync("bankMap");
        var target = Guid.NewGuid();

        // Descriptions differing only by case/whitespace normalize to the same rule.
        var put = await client.PutAsJsonAsync($"/accounts/{accountId}/bank/mappings",
            new SetBankMappingRequest("  TESCO   STORES ", "category", target));
        put.EnsureSuccessStatusCode();

        var mappings = await client.GetFromJsonAsync<List<BankMappingDto>>($"/accounts/{accountId}/bank/mappings");
        var rule = Assert.Single(mappings!);
        Assert.Equal("tesco stores", rule.MatchKey);
        Assert.Equal("category", rule.Kind);
        Assert.Equal(target, rule.TargetId);

        var del = await client.DeleteAsync($"/accounts/{accountId}/bank/mappings?description={Uri.EscapeDataString("tesco stores")}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<BankMappingDto>>($"/accounts/{accountId}/bank/mappings"))!);
    }
}
