using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Insights health read (<see cref="InsightsDto"/>) — the full financial-health report mapped server-side, with
/// language-independent narrative. This covers the Path-B addition of <c>?period=</c> so the thin client can page the
/// insights modal per viewed period, exactly as the thick modal recomputes it (keyed on the viewed period).
/// </summary>
public class InsightsApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public InsightsApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    [Fact]
    public async Task Insights_can_be_read_per_period_and_defaults_to_the_latest()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("in_period");
        var summary = await CreateAccount(client, "Insights");

        // Two periods with different outgoings: Jan spends 500, Feb (current) spends 800.
        var agg = new Account("Insights", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        var member = agg.AddMember(Guid.NewGuid(), "A");

        var jan = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.Deposit(member.UserId, new Money(2000m, "EUR"), fundId: fund);
        jan.AddExpense(new Expense(category, new Money(500m, "EUR"), new DateOnly(2026, 1, 5), member.UserId, fund));

        var feb = agg.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(member.UserId, new Money(2000m, "EUR"), fundId: fund);
        feb.AddExpense(new Expense(category, new Money(800m, "EUR"), new DateOnly(2026, 2, 5), member.UserId, fund));

        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var latest = (await client.GetFromJsonAsync<InsightsDto>($"/accounts/{summary.Id}/insights"))!;
        var febByIndex = (await client.GetFromJsonAsync<InsightsDto>($"/accounts/{summary.Id}/insights?period=1"))!;
        var janByIndex = (await client.GetFromJsonAsync<InsightsDto>($"/accounts/{summary.Id}/insights?period=0"))!;

        Assert.True(latest.HasData);
        Assert.True(janByIndex.HasData);

        // No period → the latest (Feb); ?period=1 is the same period.
        var latestCurrent = latest.Trend.Single(t => t.IsCurrent).Outgoings;
        var febCurrent = febByIndex.Trend.Single(t => t.IsCurrent).Outgoings;
        var janCurrent = janByIndex.Trend.Single(t => t.IsCurrent).Outgoings;

        Assert.Equal(latestCurrent, febCurrent);      // default == latest index
        Assert.Equal(800m, febCurrent);               // Feb's current outgoings
        Assert.Equal(500m, janCurrent);               // paging back changes the report
    }

    [Fact]
    public async Task An_out_of_range_period_falls_back_to_the_latest()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("in_oob");
        var summary = await CreateAccount(client, "OOB");

        var agg = new Account("OOB", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        var member = agg.AddMember(Guid.NewGuid(), "A");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, new Money(2000m, "EUR"), fundId: fund);
        period.AddExpense(new Expense(category, new Money(600m, "EUR"), new DateOnly(2026, 1, 5), member.UserId, fund));
        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var oob = (await client.GetFromJsonAsync<InsightsDto>($"/accounts/{summary.Id}/insights?period=99"))!;
        Assert.True(oob.HasData);   // degrades to the current period rather than erroring or emptying
    }
}
