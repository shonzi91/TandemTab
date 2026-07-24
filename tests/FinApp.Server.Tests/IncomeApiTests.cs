using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>The Path-B thin-Income read: this period's deposits with display fields resolved server-side, the
/// contribution-category + fund pickers, and the Contributed total. Period-aware via ?period={index}.</summary>
public class IncomeApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;
    public IncomeApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    [Fact]
    public async Task Income_read_lists_deposits_with_resolved_fields_and_pickers()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("inc_read");
        var summary = await CreateAccount(client, "Data");

        var agg = new Account("Data", "EUR");
        agg.AddDefaultFunds();
        var fund = agg.FundId("Bank");
        var member = agg.AddMember(Guid.NewGuid(), "Alex");
        var salary = agg.AddContributionCategory("Salary").Id;
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, new Money(2000, "EUR"), categoryId: salary, fundId: fund);

        await client.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));

        var view = await client.GetFromJsonAsync<IncomeViewDto>($"/accounts/{summary.Id}/income");

        Assert.Equal("EUR", view!.Currency);
        Assert.Equal(2000m, view.Overview.Contributed);
        var row = Assert.Single(view.Deposits);
        Assert.Equal("Alex", row.MemberName);
        Assert.Equal("Salary", row.CategoryName);
        Assert.Equal("Bank", row.FundName);
        Assert.Equal(2000m, row.Amount);
        Assert.Contains(view.Categories, c => c.Name == "Salary");
        Assert.Contains(view.Funds, f => f.Name == "Bank");
    }

    [Fact]
    public async Task Income_read_is_empty_before_any_snapshot()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("inc_empty");
        var account = await CreateAccount(client, "Fresh");

        var view = await client.GetFromJsonAsync<IncomeViewDto>($"/accounts/{account.Id}/income");
        Assert.Empty(view!.Deposits);
        Assert.Equal(0m, view.Overview.Contributed);
    }
}
