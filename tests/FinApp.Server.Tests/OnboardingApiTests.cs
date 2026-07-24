using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Path-B thin onboarding checklist (<c>GET /accounts/{id}/onboarding</c> + <c>PUT .../onboarding/dismissed</c>):
/// the four first-run steps flip Done as the account gains income/budget/expense/bucket, and dismissal persists.
/// </summary>
public class OnboardingApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public OnboardingApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Food, Guid Bank)> SeedAsync(HttpClient client, Guid accountId, Guid memberId, bool withIncome)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        if (withIncome) period.Deposit(memberId, new Money(1000m, "EUR"), fundId: bank);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, bank);
    }

    private static Task<OnboardingViewDto?> GetOnboarding(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<OnboardingViewDto>($"/accounts/{accountId}/onboarding");

    [Fact]
    public async Task Income_and_expense_steps_reflect_account_state()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ob_state");
        var account = await CreateAccount(client, "OB");
        var (food, bank) = await SeedAsync(client, account.Id, auth.UserId, withIncome: true);

        var before = (await GetOnboarding(client, account.Id))!;
        Assert.False(before.Dismissed);
        Assert.True(before.Steps.Single(s => s.Key == "income").Done);   // seeded a deposit
        Assert.False(before.Steps.Single(s => s.Key == "expense").Done); // no expense yet

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 12.5m, bank, new DateOnly(2026, 1, 10)))).EnsureSuccessStatusCode();

        var after = (await GetOnboarding(client, account.Id))!;
        Assert.True(after.Steps.Single(s => s.Key == "expense").Done);
    }

    [Fact]
    public async Task Dismiss_persists()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ob_dismiss");
        var account = await CreateAccount(client, "Dismiss");
        await SeedAsync(client, account.Id, auth.UserId, withIncome: false);

        Assert.False((await GetOnboarding(client, account.Id))!.Dismissed);

        (await client.PutAsync($"/accounts/{account.Id}/onboarding/dismissed", null)).EnsureSuccessStatusCode();

        Assert.True((await GetOnboarding(client, account.Id))!.Dismissed);
    }
}
