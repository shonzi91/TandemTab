using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Tests;

/// <summary>The Path-B thin account settings: the settings read (name/currency/savings target) and the
/// savings-target command (percent → fraction, validated).</summary>
public class ThinSettingsApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;
    public ThinSettingsApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<AccountSummaryDto> SeedAccount(HttpClient client, decimal targetFraction)
    {
        var summary = await CreateAccount(client, "Data");
        var agg = new Account("Data", "EUR");
        agg.AddDefaultFunds();
        agg.SetSavingsRateTarget(targetFraction);
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));
        return summary;
    }

    [Fact]
    public async Task Settings_read_returns_name_currency_and_target()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("set_read");
        var account = await SeedAccount(client, 0.25m);

        var s = await client.GetFromJsonAsync<AccountSettingsDto>($"/accounts/{account.Id}/settings");
        Assert.Equal("Data", s!.Name);
        Assert.Equal("EUR", s.Currency);
        Assert.Equal(0.25m, s.SavingsRateTarget);
    }

    [Fact]
    public async Task Savings_target_command_updates_the_fraction()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("set_target");
        var account = await SeedAccount(client, 0.20m);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/savings-target", new SetSavingsTargetRequest(40m));
        resp.EnsureSuccessStatusCode();

        var s = await client.GetFromJsonAsync<AccountSettingsDto>($"/accounts/{account.Id}/settings");
        Assert.Equal(0.40m, s!.SavingsRateTarget);
    }

    [Fact]
    public async Task Savings_target_out_of_range_is_rejected()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("set_bad");
        var account = await SeedAccount(client, 0.20m);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/savings-target", new SetSavingsTargetRequest(150m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
