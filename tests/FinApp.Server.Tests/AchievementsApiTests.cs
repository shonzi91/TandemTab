using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Path-B thin Achievements read (<c>GET /accounts/{id}/achievements</c>): the full catalogue with earned/locked
/// state and the tallies. Asserts the tallies agree with the item list and that logging an expense flips the
/// "first_expense" milestone from locked to earned. The catalogue itself is unit-tested in the domain.
/// </summary>
public class AchievementsApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AchievementsApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Food, Guid Bank)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.SetAchievementsAnchor(new DateOnly(2026, 1, 1));
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: bank);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, bank);
    }

    private static Task<AchievementsViewDto?> GetAwards(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AchievementsViewDto>($"/accounts/{accountId}/achievements");

    [Fact]
    public async Task Tallies_agree_with_the_item_list()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("aw_tally");
        var account = await CreateAccount(client, "Awards");
        await SeedAsync(client, account.Id, auth.UserId);

        var view = (await GetAwards(client, account.Id))!;

        Assert.True(view.Total > 0);
        Assert.Equal(view.Items.Count, view.Total);
        Assert.Equal(view.Items.Count(i => i.Earned), view.Earned);
        Assert.Equal(view.Items.Count(i => !i.Earned && i.Percent is > 0), view.InProgress);
    }

    [Fact]
    public async Task First_expense_flips_from_locked_to_earned()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("aw_first");
        var account = await CreateAccount(client, "First");
        var (food, bank) = await SeedAsync(client, account.Id, auth.UserId);

        var before = (await GetAwards(client, account.Id))!.Items.Single(i => i.Key == "first_expense");
        Assert.False(before.Earned);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 12.5m, bank, new DateOnly(2026, 1, 10)))).EnsureSuccessStatusCode();

        var after = (await GetAwards(client, account.Id))!;
        Assert.True(after.Items.Single(i => i.Key == "first_expense").Earned);
        Assert.True(after.Earned >= 1);
    }
}
