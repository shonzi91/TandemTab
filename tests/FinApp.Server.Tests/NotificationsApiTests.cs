using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Path-B thin notifications bell (<c>GET /accounts/{id}/notifications</c>): current-period domain-derived
/// alerts. Asserts "no income yet" appears on a fresh period and clears once income is logged, and that an
/// over-budget category surfaces an urgent item pointing at the Budgets tab.
/// </summary>
public class NotificationsApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public NotificationsApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static Task<NotificationsViewDto?> GetNotifications(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<NotificationsViewDto>($"/accounts/{accountId}/notifications");

    [Fact]
    public async Task No_income_notice_appears_then_clears()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("nt_income");
        var account = await CreateAccount(client, "Notif");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var bank = agg.FundId("Bank");
        agg.AddMember(auth.UserId, "Me");
        // Current-month period so it's the account's CurrentPeriod (the bell reads that).
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        agg.StartPeriod(from, from.AddMonths(1).AddDays(-1));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var before = (await GetNotifications(client, account.Id))!;
        Assert.Contains(before.Items, i => i.Text.Contains("No income", StringComparison.OrdinalIgnoreCase) && i.TargetTab == "Income");

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(Guid.Empty, bank, 500m, today))).EnsureSuccessStatusCode();

        var after = (await GetNotifications(client, account.Id))!;
        Assert.DoesNotContain(after.Items, i => i.Text.Contains("No income", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Over_budget_category_surfaces_an_urgent_item()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("nt_over");
        var account = await CreateAccount(client, "Over");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        agg.AddMember(auth.UserId, "Me");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        var period = agg.StartPeriod(from, from.AddMonths(1).AddDays(-1));
        period.Deposit(auth.UserId, new Money(1000m, "EUR"), fundId: bank);
        period.SetBudget(food, new Money(20m, "EUR"), 0.8m);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        // Spend 50 against a 20 budget -> over budget.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 50m, bank, today))).EnsureSuccessStatusCode();

        var view = (await GetNotifications(client, account.Id))!;
        var over = view.Items.FirstOrDefault(i => i.Text.Contains("over budget", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(over);
        Assert.True(over!.Urgent);
        Assert.Equal("Budgets", over.TargetTab);
        Assert.True(view.UrgentCount >= 1);
    }
}
