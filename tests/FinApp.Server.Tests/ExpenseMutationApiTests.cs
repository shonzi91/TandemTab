using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The expense command writes (POST/PUT/DELETE /accounts/{id}/expenses) — the first mutations moved server-side
/// under the Option-A migration (docs/MOBILE.md). Each write is confirmed through the /overview read, so the two
/// halves of the API prove each other: a command changes the snapshot, and the computed read reflects it.
/// </summary>
public class ExpenseMutationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ExpenseMutationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Store an initialized snapshot (funds + a Food category + an open period seeded with a deposit) and
    /// return the category and fund ids the command requests reference. Starts the snapshot at version 1.</summary>
    private static async Task<(Guid Category, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId, decimal deposit = 1000m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(deposit, "EUR"), fundId: fund);

        var resp = await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0));
        resp.EnsureSuccessStatusCode();
        return (category, fund);
    }

    private static readonly DateOnly When = new(2026, 1, 10);

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    [Fact]
    public async Task Expense_entry_history_returns_recent_manual_expenses_newest_first()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_entry");
        var account = await CreateAccount(client, "Entry");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 200m, fund, When, "Lunch"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 5m, fund, When, "Coffee"))).EnsureSuccessStatusCode();

        var entry = (await client.GetFromJsonAsync<ExpenseEntryDto>($"/accounts/{account.Id}/expense-entry"))!;

        Assert.Equal(2, entry.Recent.Count);
        Assert.Equal("Coffee", entry.Recent[0].Note);   // last-added first
        Assert.Equal(5m, entry.Recent[0].Amount);
        Assert.Equal(cat, entry.Recent[0].CategoryId);
        Assert.Equal(fund, entry.Recent[0].FundId);
        Assert.Equal("Lunch", entry.Recent[1].Note);
    }

    [Fact]
    public async Task Add_expense_advances_the_version_and_shows_in_the_overview()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_add");
        var account = await CreateAccount(client, "Data");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 200m, fund, When, "Lunch"));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;

        Assert.Equal(2, result.Version);                 // seed save made v1; this add makes v2
        Assert.NotNull(result.EntityId);
        Assert.NotEqual(Guid.Empty, result.EntityId!.Value);

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(200m, ov.Spent);
        Assert.Equal(800m, ov.Current);                  // 1000 deposit − 200 expense
    }

    [Fact]
    public async Task Successive_adds_each_advance_the_version()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_seq");
        var account = await CreateAccount(client, "Seq");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var first = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 100m, fund, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;
        var second = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 50m, fund, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        Assert.Equal(2, first.Version);
        Assert.Equal(3, second.Version);
        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(150m, ov.Spent);
    }

    [Fact]
    public async Task Edit_expense_replaces_its_amount()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_edit");
        var account = await CreateAccount(client, "Edit");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 200m, fund, When, "Lunch"))).Content.ReadFromJsonAsync<MutationResultDto>())!;
        var expenseId = add.EntityId!.Value;

        var editResp = await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}",
            new EditExpenseRequest(cat, 350m, fund, When, "Dinner"));
        editResp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(350m, ov.Spent);
        Assert.Equal(650m, ov.Current);
    }

    [Fact]
    public async Task Remove_expense_restores_the_balance()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_del");
        var account = await CreateAccount(client, "Del");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 200m, fund, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        var delResp = await client.DeleteAsync($"/accounts/{account.Id}/expenses/{add.EntityId!.Value}");
        delResp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(0m, ov.Spent);
        Assert.Equal(1000m, ov.Current);
    }

    [Fact]
    public async Task Add_expense_with_unknown_category_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_badcat");
        var account = await CreateAccount(client, "BadCat");
        var (_, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(Guid.NewGuid(), 50m, fund, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_expense_with_unknown_fund_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_badfund");
        var account = await CreateAccount(client, "BadFund");
        var (cat, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 50m, Guid.NewGuid(), When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_expense_before_any_snapshot_is_rejected()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("exp_nodata");
        var account = await CreateAccount(client, "Empty");   // created, but never initialized with a snapshot

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(Guid.NewGuid(), 50m, Guid.NewGuid(), When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Expense_time_is_stored_and_orders_a_day_newest_first()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_time");
        var account = await CreateAccount(client, "Clock");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // Same day, logged out of order, one of them with no time at all (the bank-import case).
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 5m, fund, When, "Coffee", Time: new TimeOnly(8, 15)))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 9m, fund, When, "Untimed"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 20m, fund, When, "Dinner", Time: new TimeOnly(20, 30)))).EnsureSuccessStatusCode();

        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        var sameDay = spending.Expenses.Where(e => e.Date == When).ToList();

        Assert.Equal(new[] { "Dinner", "Coffee", "Untimed" }, sameDay.Select(e => e.Note));
        Assert.Equal(new TimeOnly(20, 30), sameDay[0].Time);
        Assert.Null(sameDay[2].Time);   // never invented as midnight, and last rather than first
    }

    /// <summary>An older client's edit request carries no Time. That must leave the stored one alone rather than
    /// stripping it — correcting an amount from a phone should not silently blank the clock. Clearing is explicit.</summary>
    [Fact]
    public async Task Editing_without_a_time_keeps_it_and_clearing_is_explicit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_time_edit");
        var account = await CreateAccount(client, "Clock2");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var added = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 12m, fund, When, "Lunch", Time: new TimeOnly(13, 42))))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;

        // Edit with no Time at all — the shape an app that predates the field sends.
        var kept = (await (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{added.EntityId}",
            new EditExpenseRequest(cat, 14m, fund, When, "Lunch")))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;
        Assert.Equal(new TimeOnly(13, 42), kept.Expense!.Time);
        Assert.Equal(14m, kept.Expense.Amount);

        // ...and the deliberate "I don't actually know when" edit does blank it.
        var cleared = (await (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{kept.EntityId}",
            new EditExpenseRequest(cat, 14m, fund, When, "Lunch", ClearTime: true)))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;
        Assert.Null(cleared.Expense!.Time);
    }

    [Fact]
    public async Task Stranger_cannot_add_an_expense()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("exp_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("exp_intruder");
        var account = await CreateAccount(owner, "Private");
        var (cat, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 50m, fund, When));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);   // account invisible to a non-contributor
    }
}
