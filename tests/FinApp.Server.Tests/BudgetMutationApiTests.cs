using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Budgets — the per-category spending plan for the open period. One idempotent upsert (create-or-update, mirroring
/// <c>BudgetingState.SaveBudget</c> → <c>Period.SetBudget</c>) plus a remove. Budgets are advisory and never capped.
/// Verified through the snapshot round-trip.
/// </summary>
public class BudgetMutationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public BudgetMutationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Rent" category + an open Jan-2026 period. Returns the Rent category id.</summary>
    private static async Task<Guid> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rent = agg.AddCategory("Rent").Id;
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return rent;
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    [Fact]
    public async Task Set_budget_creates_it_with_its_threshold_and_notify_flag()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_create");
        var account = await CreateAccount(client, "Data");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}",
            new SetBudgetRequest(500m, ThresholdPercent: 75m, NotifyEvery: true))).EnsureSuccessStatusCode();

        var budget = (await LoadAsync(client, account.Id)).CurrentPeriod!.FindBudget(rent)!;
        Assert.Equal(500m, budget.Allocated.Amount);
        Assert.Equal(0.75m, budget.AlertThreshold);          // percent stored as a fraction
        Assert.True(budget.NotifyOnEveryExpense);
    }

    [Fact]
    public async Task Set_budget_is_idempotent_and_updates_in_place()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_update");
        var account = await CreateAccount(client, "Upd");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(500m))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(650m, ThresholdPercent: 90m))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.Budgets);                       // updated, not duplicated
        Assert.Equal(650m, period.FindBudget(rent)!.Allocated.Amount);
        Assert.Equal(0.90m, period.FindBudget(rent)!.AlertThreshold);
    }

    [Fact]
    public async Task Default_threshold_is_eighty_percent()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_default");
        var account = await CreateAccount(client, "Def");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(500m))).EnsureSuccessStatusCode();

        Assert.Equal(0.80m, (await LoadAsync(client, account.Id)).CurrentPeriod!.FindBudget(rent)!.AlertThreshold);
    }

    [Fact]
    public async Task Remove_budget_deletes_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_remove");
        var account = await CreateAccount(client, "Rm");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(500m))).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/accounts/{account.Id}/budgets/{rent}")).EnsureSuccessStatusCode();

        Assert.Null((await LoadAsync(client, account.Id)).CurrentPeriod!.FindBudget(rent));
    }

    [Fact]
    public async Task Removing_a_budget_that_doesnt_exist_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_rmmiss");
        var account = await CreateAccount(client, "RmMiss");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/budgets/{rent}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Budgeting_an_unknown_category_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_badcat");
        var account = await CreateAccount(client, "BadCat");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{Guid.NewGuid()}", new SetBudgetRequest(500m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_negative_budget_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_neg");
        var account = await CreateAccount(client, "Neg");
        var rent = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(-10m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Budgets_view_carries_the_essential_flag_and_the_per_category_edit_cap()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_view");
        var account = await CreateAccount(client, "View");

        // Seed two budgeted categories — Rent (essential) and Fun (discretionary, with some spend) — plus income,
        // so MaxBudget has a real reserve to work against.
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rentCat = agg.AddCategory("Rent"); rentCat.SetEssential(true);
        var funCat = agg.AddCategory("Fun");   funCat.SetEssential(false);
        agg.AddMember(auth.UserId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(auth.UserId, new Money(1000m, "EUR"));
        period.AddBudget(rentCat.Id, new Money(300m, "EUR"));
        period.AddBudget(funCat.Id, new Money(200m, "EUR"));
        period.AddExpense(new Expense(funCat.Id, new Money(50m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var view = (await client.GetFromJsonAsync<BudgetsViewDto>($"/accounts/{account.Id}/budgets"))!;

        var rent = view.Budgets.Single(b => b.CategoryId == rentCat.Id);
        var fun = view.Budgets.Single(b => b.CategoryId == funCat.Id);

        Assert.True(rent.Essential);
        Assert.False(fun.Essential);

        // Fun is a discretionary row with money left → the client's "discretionary leftovers" reallocation source.
        Assert.Equal(200m, fun.Allocated);
        Assert.Equal(50m, fun.Spent);
        Assert.Equal(150m, fun.Remaining);

        // MaxBudget is a real computed cap: never below the current allocation (you can keep your budget), and below
        // the whole balance (the other category's budget is reserved out of what's available to this one).
        Assert.True(rent.MaxBudget >= rent.Allocated);
        Assert.True(fun.MaxBudget >= fun.Allocated);
        Assert.True(rent.MaxBudget < 1000m);
        Assert.True(fun.MaxBudget < 1000m);
    }

    [Fact]
    public async Task Stranger_cannot_set_a_budget()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("bd_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("bd_intruder");
        var account = await CreateAccount(owner, "Private");
        var rent = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PutAsJsonAsync($"/accounts/{account.Id}/budgets/{rent}", new SetBudgetRequest(500m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
