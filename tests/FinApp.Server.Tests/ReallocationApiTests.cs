using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Reallocation — move a budget's spare toward savings or another budget. to-savings mirrors the web's live
/// "Move it to the loan" nudge (<c>BudgetingState.ReallocateBudgetToSaving</c>: absolute new budget + earmark, one
/// save); to-budget exposes the capped domain <c>BudgetReallocationService.ToBudget</c>. Verified via the snapshot.
/// </summary>
public class ReallocationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ReallocationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Food" budget (allocated <paramref name="foodBudget"/>, spent
    /// <paramref name="foodSpent"/>) + a "Vacations" savings bucket + an open Jan-2026 period with income.
    /// Returns (food category id, vacations bucket id, fund id).</summary>
    private static async Task<(Guid Food, Guid Vac, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId,
        decimal foodBudget = 600m, decimal foodSpent = 0m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var vac = agg.AddSavingCategory("Vacations").Id;
        agg.AddMember(memberId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"));
        period.AddBudget(food, new Money(foodBudget, "EUR"));
        if (foodSpent > 0m)
            period.AddExpense(new Expense(food, new Money(foodSpent, "EUR"), new DateOnly(2026, 1, 5), memberId, fund));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, vac, fund);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    [Fact]
    public async Task To_savings_trims_the_budget_and_earmarks_the_amount_in_one_step()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_tosav");
        var account = await CreateAccount(client, "ToSav");
        var (food, vac, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-savings",
            new ReallocateToSavingsRequest(food, NewBudget: 450m, ThresholdPercent: 80m, NotifyEvery: false,
                SavingCategoryId: vac, Amount: 150m, Date: new DateOnly(2026, 1, 10)))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(450m, period.FindBudget(food)!.Allocated.Amount);                    // budget trimmed to the absolute value
        Assert.Contains(period.SavingAllocations, a => a.SavingCategoryId == vac && a.Amount.Amount == 150m);   // earmark added
    }

    [Fact]
    public async Task To_savings_rejects_an_unknown_bucket()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_tosavbad");
        var account = await CreateAccount(client, "ToSavBad");
        var (food, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-savings",
            new ReallocateToSavingsRequest(food, 450m, 80m, false, Guid.NewGuid(), 150m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task To_budget_moves_the_leftover_between_two_budgets()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_tobud");
        var account = await CreateAccount(client, "ToBud");
        var (food, _, _) = await SeedAsync(client, account.Id, auth.UserId, foodBudget: 600m, foodSpent: 100m);

        // Add a "Fun" category + budget to receive the move.
        var fun = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Fun")));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{fun}", new SetBudgetRequest(100m))).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-budget",
            new ReallocateToBudgetRequest(food, fun, 200m))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(400m, period.FindBudget(food)!.Allocated.Amount);   // 600 - 200
        Assert.Equal(300m, period.FindBudget(fun)!.Allocated.Amount);    // 100 + 200
    }

    [Fact]
    public async Task To_budget_is_capped_at_the_source_leftover()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_cap");
        var account = await CreateAccount(client, "Cap");
        var (food, _, _) = await SeedAsync(client, account.Id, auth.UserId, foodBudget: 600m, foodSpent: 400m);
        // Leftover on food = 600 - 400 = 200.
        var fun = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Fun")));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{fun}", new SetBudgetRequest(0m))).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-budget",
            new ReallocateToBudgetRequest(food, fun, 250m));   // over the 200 leftover
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task To_budget_rejects_the_same_category_on_both_sides()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_same");
        var account = await CreateAccount(client, "Same");
        var (food, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-budget",
            new ReallocateToBudgetRequest(food, food, 100m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task To_budget_rejects_a_target_without_a_budget()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("re_notgt");
        var account = await CreateAccount(client, "NoTgt");
        var (food, _, _) = await SeedAsync(client, account.Id, auth.UserId, foodSpent: 100m);
        var fun = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Fun")));

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-budget",
            new ReallocateToBudgetRequest(food, fun, 100m));   // Fun has no budget
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_reallocate()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("re_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("re_intruder");
        var account = await CreateAccount(owner, "Private");
        var (food, vac, _) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/reallocations/to-savings",
            new ReallocateToSavingsRequest(food, 450m, 80m, false, vac, 150m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }
}
