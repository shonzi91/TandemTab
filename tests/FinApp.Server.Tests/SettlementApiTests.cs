using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Settlement / cross-account — the only commands touching TWO accounts, applied atomically through
/// <c>SnapshotService.MutateTwoAsync</c>. Transfer money to another of the caller's accounts, and settle/unsettle an
/// "on behalf" expense onto another account. Mirrors <c>BudgetingState.TransferToAccount / SettleExpenseToAccount /
/// UnsettleExpense</c>. Verified via the snapshot round-trip on both accounts.
/// </summary>
public class SettlementApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SettlementApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name, string currency = "EUR") =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, currency)))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed an account: default funds + a "Food" category + an open Jan-2026 period, the "Bank" fund opened
    /// at <paramref name="opening"/>. If <paramref name="expense"/> &gt; 0, adds a Food expense from Bank. Returns
    /// (food category, bank fund, expenseId-or-Empty).</summary>
    private static async Task<(Guid Food, Guid Fund, Guid ExpenseId)> SeedAsync(HttpClient client, Guid accountId, Guid memberId,
        string currency = "EUR", decimal opening = 0m, decimal expense = 0m)
    {
        var agg = new Account("Seed", currency);
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        agg.AddMember(memberId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        if (opening > 0m) period.SetInitialBalance(fund, new Money(opening, currency));
        var expenseId = Guid.Empty;
        if (expense > 0m)
        {
            var e = new Expense(food, new Money(expense, currency), new DateOnly(2026, 1, 5), memberId, fund, "On behalf", onBehalfOfOtherAccount: true);
            period.AddExpense(e);
            expenseId = e.Id;
        }
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, fund, expenseId);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task Transfer_records_an_outflow_here_and_a_deposit_there()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_xfer");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, fund, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, fund, 300m, Note: "rent share")));

        var sourcePeriod = (await LoadAsync(client, source.Id)).CurrentPeriod!;
        Assert.Contains(sourcePeriod.ExternalTransfers, t => t.Amount.Amount == 300m && t.ToAccountId == dest.Id);
        Assert.Equal(700m, sourcePeriod.FundBalance(fund).Amount);   // 1000 opening - 300 out

        var destPeriod = (await LoadAsync(client, dest.Id)).CurrentPeriod!;
        Assert.Equal(300m, destPeriod.Contributions.Where(c => c.MemberId == auth.UserId).Sum(c => c.Paid.Amount));
    }

    [Fact]
    public async Task Transfer_is_capped_at_the_source_fund_balance()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cap");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, fund, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 100m);
        await SeedAsync(client, dest.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, fund, 500m));   // only 100 in the fund
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Transfer_between_different_currencies_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_ccy");
        var source = await CreateAccount(client, "Source", "EUR");
        var dest = await CreateAccount(client, "Dest", "USD");
        var (_, fund, _) = await SeedAsync(client, source.Id, auth.UserId, "EUR", opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId, "USD");

        var resp = await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, fund, 300m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Settle_creates_a_linked_expense_there_and_reduces_the_source()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_settle");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, _, expenseId) = await SeedAsync(client, source.Id, auth.UserId, expense: 120m);
        var (destFood, destFund, _) = await SeedAsync(client, dest.Id, auth.UserId);

        var settlementId = await IdOf(await client.PostAsJsonAsync($"/accounts/{source.Id}/expenses/{expenseId}/settle",
            new SettleExpenseRequest(dest.Id, destFund, destFood, 50m)));

        // Source: the expense is reduced to 70 and tagged with the link (its id changes on re-creation).
        var sourceExpense = (await LoadAsync(client, source.Id)).CurrentPeriod!.Expenses.Single();
        Assert.Equal(70m, sourceExpense.Amount.Amount);
        Assert.Equal(120m, sourceExpense.OriginalAmount.Amount);
        Assert.True(sourceExpense.IsSettlementSource);
        Assert.Equal(dest.Id, sourceExpense.SettledToAccountId);
        Assert.Equal(settlementId, sourceExpense.SettlementId);

        // Destination: a new 50 expense linked back to the source.
        var destExpense = (await LoadAsync(client, dest.Id)).CurrentPeriod!.Expenses.Single();
        Assert.Equal(50m, destExpense.Amount.Amount);
        Assert.Equal(settlementId, destExpense.SettlementId);
        Assert.True(destExpense.IsSettlementDestination);
        Assert.Equal(source.Id, destExpense.SettledFromAccountId);
    }

    [Fact]
    public async Task Settling_more_than_the_expense_amount_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_over");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, _, expenseId) = await SeedAsync(client, source.Id, auth.UserId, expense: 120m);
        var (destFood, destFund, _) = await SeedAsync(client, dest.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{source.Id}/expenses/{expenseId}/settle",
            new SettleExpenseRequest(dest.Id, destFund, destFood, 200m));   // expense is only 120
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unsettle_removes_the_linked_expense_and_restores_the_source()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_unsettle");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, _, expenseId) = await SeedAsync(client, source.Id, auth.UserId, expense: 120m);
        var (destFood, destFund, _) = await SeedAsync(client, dest.Id, auth.UserId);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{source.Id}/expenses/{expenseId}/settle",
            new SettleExpenseRequest(dest.Id, destFund, destFood, 50m)));
        // The source expense id changed on settle — grab the current one.
        var settled = (await LoadAsync(client, source.Id)).CurrentPeriod!.Expenses.Single();

        (await client.DeleteAsync($"/accounts/{source.Id}/expenses/{settled.Id}/settle?destinationAccountId={dest.Id}")).EnsureSuccessStatusCode();

        var sourceExpense = (await LoadAsync(client, source.Id)).CurrentPeriod!.Expenses.Single();
        Assert.Equal(120m, sourceExpense.Amount.Amount);          // full amount restored
        Assert.False(sourceExpense.IsSettlementSource);
        Assert.Empty((await LoadAsync(client, dest.Id)).CurrentPeriod!.Expenses);   // linked expense gone
    }

    /// <summary>
    /// ★ A THIN client must be able to undo a settlement using only what it can read. The undo route is addressed by
    /// the destination account id, and until Session 108 the thin <c>ExpenseDto</c> carried the two settlement
    /// booleans but not <c>SettledToAccountId</c> — so the phone could see that an expense was settled and had no way
    /// to name the account it was settled onto. The undo was unreachable by construction, the same shape S105 found
    /// three times. This test walks the whole loop through <c>/spending</c> only, never the snapshot.
    /// </summary>
    [Fact]
    public async Task A_thin_client_can_read_the_settlement_target_and_undo_with_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_thin");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, _, expenseId) = await SeedAsync(client, source.Id, auth.UserId, expense: 120m);
        var (destFood, destFund, _) = await SeedAsync(client, dest.Id, auth.UserId);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{source.Id}/expenses/{expenseId}/settle",
            new SettleExpenseRequest(dest.Id, destFund, destFood, 50m)));

        // The source side, as a thin client sees it: who it is with, and for how much.
        var sourceRow = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{source.Id}/spending"))!.Expenses.Single();
        Assert.True(sourceRow.IsSettlementSource);
        Assert.Equal(dest.Id, sourceRow.SettledToAccountId);
        Assert.Equal(50m, sourceRow.SettledAmount);
        Assert.Null(sourceRow.SettledFromAccountId);

        // The destination side names the account the money came from, so its row can label itself too.
        var destRow = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{dest.Id}/spending"))!.Expenses.Single();
        Assert.True(destRow.IsSettlementDestination);
        Assert.Equal(source.Id, destRow.SettledFromAccountId);

        // The undo, addressed purely from the read model — this is the call that was impossible before.
        (await client.DeleteAsync(
            $"/accounts/{source.Id}/expenses/{sourceRow.Id}/settle?destinationAccountId={sourceRow.SettledToAccountId}"))
            .EnsureSuccessStatusCode();

        var restored = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{source.Id}/spending"))!.Expenses.Single();
        Assert.Equal(120m, restored.Amount);
        Assert.False(restored.IsSettlementSource);
        Assert.Null(restored.SettledToAccountId);
        Assert.Equal(0m, restored.SettledAmount);
        Assert.Empty((await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{dest.Id}/spending"))!.Expenses);
    }

    [Fact]
    public async Task Transferring_to_the_same_account_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_self");
        var source = await CreateAccount(client, "Source");
        var (_, fund, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);

        var resp = await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(source.Id, fund, 100m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_transfer_between_accounts_they_dont_belong_to()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("st_intruder");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (_, fund, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, fund, 100m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
