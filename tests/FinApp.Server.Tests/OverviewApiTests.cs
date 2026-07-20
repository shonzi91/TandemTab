using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Tests;

public class OverviewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public OverviewApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    [Fact]
    public async Task Overview_is_empty_before_any_snapshot()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("ov_empty");
        var account = await CreateAccount(client, "Fresh");

        var ov = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview");
        Assert.Equal(0m, ov!.Current);
        Assert.Equal(0m, ov.SafeAfterBills);
    }

    [Fact]
    public async Task Overview_computes_the_header_figures_from_the_saved_snapshot()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("ov_data");
        var summary = await CreateAccount(client, "Data");

        // Build a domain account with known figures and store it as the snapshot the server will read.
        var agg = new Account("Data", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        var member = agg.AddMember(Guid.NewGuid(), "A");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, new Money(2000, "EUR"), fundId: fund);
        period.AddExpense(new Expense(category, new Money(500, "EUR"), new DateOnly(2026, 1, 5), member.UserId, fund));
        period.AllocateToSavings(Guid.NewGuid(), new Money(300, "EUR"), new DateOnly(2026, 1, 6));
        agg.AddRecurring(new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 200m, 15, category, fund));

        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var ov = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{summary.Id}/overview");

        Assert.Equal("EUR", ov!.Currency);
        Assert.Equal(1500m, ov.Current);
        Assert.Equal(1200m, ov.Free);
        Assert.Equal(300m, ov.Saved);
        Assert.Equal(500m, ov.Spent);
        Assert.Equal(2000m, ov.Contributed);
        Assert.Equal(200m, ov.BillsDue);
        Assert.Equal(1000m, ov.SafeAfterBills);
    }

    [Fact]
    public async Task Stranger_cannot_read_the_overview()
    {
        var (alice, _) = await _factory.RegisterAndAuthAsync("ov_alice");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("ov_stranger");
        var account = await CreateAccount(alice, "Private");
        await alice.PutAsJsonAsync($"/accounts/{account.Id}/snapshot", new SaveAccountRequest("{}", 0));

        var resp = await stranger.GetAsync($"/accounts/{account.Id}/overview");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // account invisible to a non-contributor
    }
}
