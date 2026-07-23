using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// The server-side account bootstrap (POST /accounts/{id}/bootstrap) — the thin-client counterpart of the web app's
/// first-load seed, so a native client can create an account without carrying the domain. Verified through the
/// snapshot + overview reads, and asserted to seed the same starter body the web client does (they share
/// <c>Account.SeedStarter</c>).
/// </summary>
public class BootstrapApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public BootstrapApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    [Fact]
    public async Task Bootstrap_seeds_the_starter_body_and_a_current_month_period()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("bs_seed");
        var account = await CreateAccount(client, "Fresh");

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/bootstrap",
            new BootstrapAccountRequest(new DateOnly(2026, 3, 14)));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;
        Assert.Equal(1, result.Version);   // the seed is v1

        // Confirm the seeded body through the raw snapshot: 4 categories, 2 contribution categories, 4 funds, 1 period.
        var snap = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot"))!;
        var agg = AccountSnapshotSerializer.Deserialize(snap.Payload);
        Assert.Equal(new[] { "Food", "Bills", "Transport", "Other" }, agg.Categories.Select(c => c.Name));
        Assert.Equal(new[] { "Salary", "Other" }, agg.ContributionCategories.Select(c => c.Name));
        Assert.Equal(new[] { "Bank", "Cash", "Digital wallet", "Other" }, agg.Funds.Select(f => f.Name));
        Assert.Single(agg.Periods);
        Assert.Equal(new DateOnly(2026, 3, 1), agg.Periods[0].From);
        Assert.Equal(new DateOnly(2026, 3, 31), agg.Periods[0].To);
        Assert.NotNull(agg.AchievementsAnchor);
        Assert.Equal(new DateOnly(2026, 3, 1), agg.AchievementsAnchor);
    }

    [Fact]
    public async Task Bootstrapped_account_reports_an_empty_but_present_overview()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("bs_overview");
        var account = await CreateAccount(client, "Fresh");

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/bootstrap", new BootstrapAccountRequest())).EnsureSuccessStatusCode();

        var ov = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;
        Assert.Equal("EUR", ov.Currency);   // a real (initialized) period exists, so the currency is carried
        Assert.Equal(0m, ov.Current);
        Assert.Equal(0m, ov.Spent);
    }

    [Fact]
    public async Task Bootstrap_is_rejected_when_the_account_is_already_set_up()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("bs_twice");
        var account = await CreateAccount(client, "Fresh");

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/bootstrap", new BootstrapAccountRequest())).EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync($"/accounts/{account.Id}/bootstrap", new BootstrapAccountRequest());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_without_a_body_uses_the_server_date()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("bs_nobody");
        var account = await CreateAccount(client, "Fresh");

        // No JSON body at all — the endpoint must still bootstrap (server falls back to its own UTC date).
        var resp = await client.PostAsync($"/accounts/{account.Id}/bootstrap", content: null);
        resp.EnsureSuccessStatusCode();

        var snap = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot"))!;
        Assert.Single(AccountSnapshotSerializer.Deserialize(snap.Payload).Periods);
    }

    [Fact]
    public async Task Stranger_cannot_bootstrap_an_account()
    {
        var (owner, _) = await _factory.RegisterAndAuthAsync("bs_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("bs_intruder");
        var account = await CreateAccount(owner, "Private");

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/bootstrap", new BootstrapAccountRequest());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
