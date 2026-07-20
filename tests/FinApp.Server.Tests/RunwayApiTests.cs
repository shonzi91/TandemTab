using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Tests;

public class RunwayApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RunwayApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    [Fact]
    public async Task Runway_projects_from_recurring_items()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("rw_data");
        var summary = await CreateAccount(client, "Runway");

        var agg = new Account("Runway", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Rent").Id;
        var fund = agg.FundId("Bank");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        agg.AddRecurring(new RecurringItem("Salary", RecurringKind.Income, RecurringAmountMode.Fixed, 3000m, 25, category, fund));
        agg.AddRecurring(new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 1000m, 1, category, fund));

        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var rw = await client.GetFromJsonAsync<RunwayDto>($"/accounts/{summary.Id}/runway");

        Assert.NotNull(rw);
        Assert.True(rw!.BasedOnRecurring);
        Assert.Equal(3000m, rw.MonthlyIncome);
        Assert.Equal(1000m, rw.MonthlySpending);
        Assert.Null(rw.FirstShortfallMonth); // income exceeds spending → never runs short
    }

    [Fact]
    public async Task Runway_is_null_without_a_basis()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("rw_none");
        var summary = await CreateAccount(client, "NoBasis");

        var agg = new Account("NoBasis", "EUR");
        agg.AddDefaultFunds();
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var resp = await client.GetAsync($"/accounts/{summary.Id}/runway");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode); // no basis → nothing to show
    }
}
