using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Money back on an expense — a refund, or a friend paying their share of a bill you covered, arriving as a bank
/// credit. The expense shrinks and nothing is booked as income.
///
/// <para>★ Every assertion here reads <c>/spending</c>, never the snapshot. The thick web client pushes whole
/// aggregates and would pass these tests without the route existing at all; the whole point of the route is that a
/// phone can do this, so the tests are written the way a phone has to work.</para>
/// </summary>
public class RefundApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RefundApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Default funds + a Food category + an open Jan-2026 period holding one Food expense from Bank.</summary>
    private static async Task<Guid> SeedAsync(HttpClient client, Guid accountId, Guid memberId, decimal expense)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        agg.AddMember(memberId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var e = new Expense(food, new Money(expense, "EUR"), new DateOnly(2026, 1, 5), memberId, fund, "Dinner");
        period.AddExpense(e);
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return e.Id;
    }

    private static async Task<SpendingViewDto> SpendingAsync(HttpClient client, Guid accountId) =>
        (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{accountId}/spending"))!;

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task A_refund_reduces_the_expense_and_adds_no_income()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_basic");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        var newId = await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m)));

        var view = await SpendingAsync(client, account.Id);
        var row = view.Expenses.Single();
        Assert.Equal(40m, row.Amount);
        Assert.Equal(20m, row.RefundedAmount);
        // ★ The reason the feature exists rather than "just log it as income": spending falls, and money-in does not
        // move. Booked as a contribution this would read €60 spent and €20 in — two wrong figures that net out.
        Assert.Equal(40m, view.Expenses.Sum(e => e.Amount));

        // ⚠️ The id changed, and the response is the only place the caller can learn the new one.
        Assert.NotEqual(expenseId, newId);
        Assert.Equal(newId, row.Id);
    }

    [Fact]
    public async Task Two_refunds_accumulate_because_the_body_is_a_delta_not_a_total()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_delta");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        // Two friends pay their shares on different days. ★ Each call states only what arrived, so the second does
        // not need to have read the first — which is what stops one phone's stale total erasing the other's refund.
        var afterFirst = await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m)));
        await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{afterFirst}/refund", new RefundExpenseRequest(15m)));

        var row = (await SpendingAsync(client, account.Id)).Expenses.Single();
        Assert.Equal(25m, row.Amount);
        Assert.Equal(35m, row.RefundedAmount);
    }

    [Fact]
    public async Task A_thin_client_can_see_a_refund_and_undo_it_with_what_it_read()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_thin");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m)));

        // Everything the undo needs comes off /spending: the current id, and the fact there is anything to undo.
        var refunded = (await SpendingAsync(client, account.Id)).Expenses.Single();
        Assert.True(refunded.RefundedAmount > 0m);

        (await client.DeleteAsync($"/accounts/{account.Id}/expenses/{refunded.Id}/refund")).EnsureSuccessStatusCode();

        var restored = (await SpendingAsync(client, account.Id)).Expenses.Single();
        Assert.Equal(60m, restored.Amount);
        Assert.Equal(0m, restored.RefundedAmount);
    }

    [Fact]
    public async Task A_refund_keeps_the_rows_note_category_and_fund()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_carry");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);
        var before = (await SpendingAsync(client, account.Id)).Expenses.Single();

        await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m)));

        // The rebuild mints a new row, so anything not carried across vanishes silently. A phone showing "Dinner"
        // under Food from Bank must still show that afterwards, or the refund reads as a different expense.
        var after = (await SpendingAsync(client, account.Id)).Expenses.Single();
        Assert.Equal(before.Note, after.Note);
        Assert.Equal(before.CategoryId, after.CategoryId);
        Assert.Equal(before.FundId, after.FundId);
        Assert.Equal(before.Date, after.Date);
    }

    [Fact]
    public async Task Refunding_more_than_the_charge_is_refused_and_says_the_ceiling()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_over");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        var tooMuch = await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(61m));
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("60", await tooMuch.Content.ReadAsStringAsync());   // the message names the ceiling

        var zero = await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(0m));
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        // Nothing partial was written by either refusal.
        var row = (await SpendingAsync(client, account.Id)).Expenses.Single();
        Assert.Equal(60m, row.Amount);
        Assert.Equal(0m, row.RefundedAmount);
    }

    [Fact]
    public async Task Undoing_a_refund_that_never_happened_is_refused()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_noundo");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/expenses/{expenseId}/refund");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Cross-period (S119, owner report) ──────────────────────────────────────────────────────────────────

    /// <summary>June's dinner, then July and August rolled on top with the closing balance carried forward each
    /// time — the snapshot behaviour that makes a retroactive edit to June invisible to August.</summary>
    private static async Task<(Guid ExpenseId, Guid Cash)> SeedOlderExpenseAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        agg.AddMember(memberId, "Me");
        var cash = agg.FundId("Cash");

        var june = agg.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        june.SetInitialBalance(cash, new Money(500m, "EUR"));
        var dinner = new Expense(food, new Money(60m, "EUR"), new DateOnly(2026, 6, 12), memberId, cash, "Group dinner");
        june.AddExpense(dinner);
        var july = agg.StartPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        july.SetInitialBalance(cash, june.ExpectedClosingBalance);
        var august = agg.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        august.SetInitialBalance(cash, july.ExpectedClosingBalance);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (dinner.Id, cash);
    }

    [Fact]
    public async Task Money_can_come_back_on_an_expense_from_two_periods_ago()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_older");
        var account = await CreateAccount(client, "Personal");
        var (expenseId, cash) = await SeedOlderExpenseAsync(client, account.Id, auth.UserId);

        var overviewBefore = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;

        var resp = await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m, cash));
        resp.EnsureSuccessStatusCode();

        // ★★ The half that matters: the money has to be visible in the period it actually arrived in. June's
        // ledger being corrected is necessary and not sufficient — nothing carries a retroactive credit forward.
        var overviewAfter = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;
        Assert.Equal(overviewBefore.Current + 20m, overviewAfter.Current);
        // ...and August's own spending is untouched: the charge was June's and stays June's.
        Assert.Equal(overviewBefore.Spent, overviewAfter.Spent);

        // ★★ **The refund is still not income**, which is the promise the whole design rests on: `Contributed`
        // (fresh income this period) does not move. What does move is `MoneyIn`, whose other half is carry-in —
        // and that is exactly what this €20 is: money from an earlier period that is available now.
        Assert.Equal(overviewBefore.Contributed, overviewAfter.Contributed);
        Assert.Equal(overviewBefore.MoneyIn + 20m, overviewAfter.MoneyIn);
        // ⚠️ And the books still balance. This is the reason the credit goes into the opening balance rather than
        // straight onto `Current`: a balance that grew without anything on the money-in side growing with it would
        // no longer reconcile against its own inputs, and the hero would quietly stop adding up.
        Assert.Equal(overviewAfter.MoneyIn - overviewAfter.Spent - overviewAfter.TransfersOut, overviewAfter.Current);
    }

    [Fact]
    public async Task Undoing_an_older_refund_leaves_the_account_where_it_started()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_older_undo");
        var account = await CreateAccount(client, "Personal");
        var (expenseId, cash) = await SeedOlderExpenseAsync(client, account.Id, auth.UserId);

        var before = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;

        var newId = await IdOf(await client.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(20m, cash)));
        (await client.DeleteAsync($"/accounts/{account.Id}/expenses/{newId}/refund")).EnsureSuccessStatusCode();

        // ⚠️ An undo that only put the expense back would leave the account permanently €20 richer — a worse bug
        // than the one the cross-period refund fixes, and a silent one.
        var after = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;
        Assert.Equal(before.Current, after.Current);
    }

    [Fact]
    public async Task A_stranger_cannot_refund_someone_elses_expense()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rf_owner");
        var account = await CreateAccount(client, "Personal");
        var expenseId = await SeedAsync(client, account.Id, auth.UserId, expense: 60m);

        var (stranger, _) = await _factory.RegisterAndAuthAsync("rf_stranger");
        var resp = await stranger.PostAsJsonAsync(
            $"/accounts/{account.Id}/expenses/{expenseId}/refund", new RefundExpenseRequest(10m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
