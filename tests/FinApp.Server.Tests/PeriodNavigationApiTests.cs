using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>The Path-B thin period navigation: the periods list read + the ?period={index} selector on the
/// period-scoped surface reads (a past period reads its own figures; an absent/out-of-range index falls back to
/// the current period).</summary>
public class PeriodNavigationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;
    public PeriodNavigationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    // A two-period account: Jan (closed, one 500 expense) then Feb (open, one 100 expense).
    private static async Task<AccountSummaryDto> SeedTwoPeriods(HttpClient client)
    {
        var summary = await CreateAccount(client, "Two");
        var agg = new Account("Two", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        var member = agg.AddMember(Guid.NewGuid(), "A");

        var jan = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AddExpense(new Expense(category, new Money(500, "EUR"), new DateOnly(2026, 1, 5), member.UserId, fund));
        jan.Close();
        var feb = agg.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.AddExpense(new Expense(category, new Money(100, "EUR"), new DateOnly(2026, 2, 3), member.UserId, fund));

        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));
        return summary;
    }

    [Fact]
    public async Task Periods_list_reports_dates_open_and_latest_flags()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("pn_list");
        var account = await SeedTwoPeriods(client);

        var view = await client.GetFromJsonAsync<PeriodsViewDto>($"/accounts/{account.Id}/periods");

        Assert.Equal(1, view!.CurrentIndex);
        Assert.Equal(2, view.Periods.Count);
        Assert.False(view.Periods[0].IsOpen);
        Assert.False(view.Periods[0].IsLatest);
        Assert.Equal(new DateOnly(2026, 1, 1), view.Periods[0].From);
        Assert.True(view.Periods[1].IsOpen);
        Assert.True(view.Periods[1].IsLatest);
    }

    [Fact]
    public async Task Overview_defaults_to_current_period_but_period_index_selects_a_past_one()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("pn_ov");
        var account = await SeedTwoPeriods(client);

        var current = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview");
        var past = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview?period=0");

        Assert.Equal(100m, current!.Spent);   // Feb (open) — the default
        Assert.Equal(500m, past!.Spent);       // Jan (closed) — selected by index
    }

    [Fact]
    public async Task Spending_read_returns_the_selected_period_expenses()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("pn_sp");
        var account = await SeedTwoPeriods(client);

        var current = await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending");
        var past = await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending?period=0");

        Assert.Equal(100m, Assert.Single(current!.Expenses).Amount);
        Assert.Equal(500m, Assert.Single(past!.Expenses).Amount);
    }

    [Fact]
    public async Task Out_of_range_period_index_falls_back_to_current()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("pn_oor");
        var account = await SeedTwoPeriods(client);

        var past = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview?period=99");
        Assert.Equal(100m, past!.Spent);   // clamped to the current period, not an error
    }
}
