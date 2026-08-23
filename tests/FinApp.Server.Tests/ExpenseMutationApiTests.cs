using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
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

    /// <summary>
    /// ★ Owner report: putting a label on an auto-filed row made its 🏦 badge disappear. Editing an expense used to
    /// keep <c>BankExternalId</c> but deliberately clear <c>AutoFiled</c>. The badge answers "where did this row come
    /// from", which an edit does not change — and clearing it also hid the edit modal's rule shortcut at exactly the
    /// moment it is most wanted, when you are correcting a row the rule mis-filed.
    /// </summary>
    [Fact]
    public async Task Editing_an_auto_filed_expense_keeps_its_bank_badge()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_autofiled");
        var account = await CreateAccount(client, "Bank");

        // Seed a row that came in from the bank: an external id AND the auto-filed marker.
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var cat = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(auth.UserId, "Me");
        var seeded = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        seeded.Deposit(auth.UserId, new Money(1000m, "EUR"), fundId: fund);
        var imported = seeded.AddExpense(new Expense(cat, new Money(30m, "EUR"), When, auth.UserId, fund, "TESCO"));
        imported.SetBankLink("bank-tx-77", autoFiled: true);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var tag = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Weekly")))
            .Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        // Label it through the edit form — the path the report came from.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{imported.Id}",
            new EditExpenseRequest(cat, 30m, fund, When, "TESCO", TagId: tag))).EnsureSuccessStatusCode();

        var after = AccountSnapshotSerializer.Deserialize(
            (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{account.Id}/snapshot"))!.Payload)
            .Periods[0].Expenses.Single();
        Assert.True(after.AutoFiled);                     // the badge — this is the bug
        Assert.Equal("bank-tx-77", after.BankExternalId); // provenance, which already survived
        Assert.Equal(tag, after.TagId);
    }

    // ── T0: idempotency ────────────────────────────────────────────────────────────────────────────────────
    // The failure these exist for is the ordinary one on a bad connection: the request lands, the response is
    // lost, and the client cannot tell that from a request that never arrived. Retrying is all it can do.

    [Fact]
    public async Task Retrying_an_add_with_the_same_key_logs_one_expense_and_answers_the_same_way()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_idem");
        var account = await CreateAccount(client, "Retry");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();

        var first = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 42m, fund, When, "Dinner", ClientId: key)))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;
        var second = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 42m, fund, When, "Dinner", ClientId: key)))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;

        // One row, and the retry answered with the original's id — which is what makes it look like success.
        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.Single(spending.Expenses);
        Assert.Equal(first.EntityId, second.EntityId);
        Assert.Equal(42m, (await Overview(client, account.Id))!.Spent);
        // ...and it wrote nothing: same version, so no other client is told to re-pull for a change that did not
        // happen. This is the half a naive "return the existing row" would still get wrong.
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task Two_expenses_that_look_alike_both_land_when_their_keys_differ()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_idem_two");
        var account = await CreateAccount(client, "Coffees");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // Two €3 coffees on one day are not a duplicate — which is exactly why the key is not a content hash.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 3m, fund, When, "Coffee", ClientId: Guid.NewGuid()))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 3m, fund, When, "Coffee", ClientId: Guid.NewGuid()))).EnsureSuccessStatusCode();

        Assert.Equal(2, (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!.Expenses.Count);
        Assert.Equal(6m, (await Overview(client, account.Id))!.Spent);
    }

    [Fact]
    public async Task A_write_without_a_key_makes_no_claim_about_duplicates()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_idem_none");
        var account = await CreateAccount(client, "Legacy");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // Every client that predates T0 sends none, and must keep working exactly as before — including its
        // ability to log the same figure twice on purpose.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 8m, fund, When, "Bus"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 8m, fund, When, "Bus"))).EnsureSuccessStatusCode();

        Assert.Equal(2, (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!.Expenses.Count);
    }

    [Fact]
    public async Task A_retry_that_arrives_after_the_row_was_edited_still_finds_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_idem_edited");
        var account = await CreateAccount(client, "Edited");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();

        var added = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 20m, fund, When, "Lunch", ClientId: key)))
            .Content.ReadFromJsonAsync<ExpenseMutationDto>())!;

        // An edit REBUILDS the row (append-only ledger, new id). If the key were not carried across, the retry
        // below would find nothing and log a second lunch — months after anyone could connect the two.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{added.EntityId}",
            new EditExpenseRequest(cat, 25m, fund, When, "Lunch"))).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 20m, fund, When, "Lunch", ClientId: key))).EnsureSuccessStatusCode();

        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.Single(spending.Expenses);
        Assert.Equal(25m, (await Overview(client, account.Id))!.Spent);   // the edit stands; the retry changed nothing
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
