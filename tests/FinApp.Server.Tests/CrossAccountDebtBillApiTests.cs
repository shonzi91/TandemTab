using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// D2: a recurring bill in one account servicing a debt bucket in another. Unlike a cross-account trip this is a
/// genuine two-account <b>write on every post</b> — the expense rows land in the account that paid, the balance
/// moves in the account that owes — so the theme here is that both halves commit, and that the undo can find its
/// way back.
/// </summary>
public class CrossAccountDebtBillApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public CrossAccountDebtBillApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly PeriodFrom = new(2026, 6, 1);
    private static readonly DateOnly PeriodTo = new(2026, 6, 30);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name, string currency = "EUR") =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, currency)))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Category, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId,
        string categoryName = "Loan", string currency = "EUR")
    {
        var agg = new Account("Seed", currency);
        agg.AddDefaultFunds();
        var category = agg.AddCategory(categoryName).Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(PeriodFrom, PeriodTo).Deposit(memberId, new Money(5000m, currency), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    private static async Task<Account> Snapshot(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    /// <summary>"Mine" holds the bill and pays it; "Joint" owns the €20,000 loan at 6% with a €600 installment.</summary>
    private async Task<(HttpClient Client, Guid Payer, Guid Owner, Guid Loan, Guid PayerCategory, Guid PayerFund)>
        TwoAccountsAsync(string seed, decimal installment = 600m)
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync(seed);
        var payer = await CreateAccount(client, "Mine");
        var owner = await CreateAccount(client, "Joint");
        var (payerCat, payerFund) = await SeedAsync(client, payer.Id, auth.UserId, "Loan");
        await SeedAsync(client, owner.Id, auth.UserId, "Housing");

        var loan = await IdOf(await client.PostAsJsonAsync($"/accounts/{owner.Id}/savings/buckets",
            new SaveSavingBucketRequest("Mortgage", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m,
                DebtInstallment: installment)));
        return (client, payer.Id, owner.Id, loan, payerCat, payerFund);
    }

    private static Task<Guid> LinkBill(HttpClient client, Guid payer, Guid owner, Guid loan, Guid cat, Guid fund,
        decimal expected = 600m, Guid? excessCategoryId = null) =>
        IdOf(client.PostAsJsonAsync($"/accounts/{payer}/recurring",
            new AddRecurringRequest("Mortgage", "expense", "fixed", expected, 10, cat, fund,
                LinkedDebtBucketId: loan, ExcessCategoryId: excessCategoryId, LinkedDebtAccountId: owner)).Result);

    [Fact]
    public async Task Linking_a_bill_to_another_accounts_loan_writes_both_sides()
    {
        // ★ Even CREATING is two-account: the loan's installment day and payment-driven flag are set on its side.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_link");

        var billId = await LinkBill(client, payer, owner, loan, cat, fund);

        var bill = (await Snapshot(client, payer)).FindRecurring(billId)!;
        Assert.Equal(loan, bill.LinkedDebtBucketId);
        Assert.Equal(owner, bill.LinkedDebtAccountId);
        Assert.True(bill.IsCrossAccountInstallment);

        var bucket = (await Snapshot(client, owner)).FindSavingCategory(loan)!;
        Assert.True(bucket.DebtPaymentDriven);      // linking says "I pay this from the app"
        Assert.Equal(10, bucket.DebtInstallmentDay); // ...and the bill's day filled the loan's blank
    }

    [Fact]
    public async Task Confirming_posts_the_rows_here_and_moves_the_balance_there()
    {
        // ★ The whole point, as numbers: 20,000 at 6% is 100 of interest, so 600 pays 500 of principal.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_post");
        var billId = await LinkBill(client, payer, owner, loan, cat, fund);

        (await client.PostAsJsonAsync($"/accounts/{payer}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(600m))).EnsureSuccessStatusCode();

        // The expense rows are in the account that PAID.
        var rows = (await Snapshot(client, payer)).CurrentPeriod!.Expenses.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(100m, rows.Single(r => r.Part == InstallmentPart.Interest).Amount.Amount);
        Assert.Equal(500m, rows.Single(r => r.Part == InstallmentPart.Principal).Amount.Amount);
        // ...and each remembers where its loan lives, or the undo could never find it again.
        Assert.All(rows, r => Assert.Equal(owner, r.DebtBucketAccountId));

        // The balance moved in the account that OWES.
        Assert.Equal(19500m, (await Snapshot(client, owner)).FindSavingCategory(loan)!.DebtBalance);
        // And the owning account's own SPENDING did not move — it did not pay anything.
        Assert.Equal(0m, (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{owner}/overview"))!.Spent);
        Assert.Equal(600m, (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{payer}/overview"))!.Spent);
    }

    [Fact]
    public async Task The_excess_line_still_works_across_the_boundary()
    {
        // Item C's rule reads DebtInstallment from the OTHER account now. A 700 bill on a 600 loan books 600 of
        // servicing here and 100 as insurance — the cap has to survive the trip across.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_excess");
        var insurance = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer}/categories",
            new CreateCategoryRequest("Insurance")));
        var billId = await LinkBill(client, payer, owner, loan, cat, fund, expected: 700m, excessCategoryId: insurance);

        (await client.PostAsJsonAsync($"/accounts/{payer}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(700m))).EnsureSuccessStatusCode();

        var rows = (await Snapshot(client, payer)).CurrentPeriod!.Expenses.ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(100m, rows.Single(r => r.Part == InstallmentPart.Additional).Amount.Amount);
        Assert.Equal(insurance, rows.Single(r => r.Part == InstallmentPart.Additional).CategoryId);
        // Serviced at the contractual 600, not at the 700 that left the account.
        Assert.Equal(19500m, (await Snapshot(client, owner)).FindSavingCategory(loan)!.DebtBalance);
    }

    [Fact]
    public async Task Undoing_the_installment_reverses_the_balance_in_the_other_account()
    {
        // ⚠️ Without DebtBucketAccountId on the rows this is the half-undo: the ledger rows vanish while the other
        // account's balance keeps the payment.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_undo");
        var billId = await LinkBill(client, payer, owner, loan, cat, fund);
        (await client.PostAsJsonAsync($"/accounts/{payer}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(600m))).EnsureSuccessStatusCode();
        var groupId = (await Snapshot(client, payer)).CurrentPeriod!.Expenses.First().InstallmentGroupId!.Value;

        (await client.DeleteAsync($"/accounts/{payer}/installments/{groupId}")).EnsureSuccessStatusCode();

        Assert.Empty((await Snapshot(client, payer)).CurrentPeriod!.Expenses);
        Assert.Equal(20000m, (await Snapshot(client, owner)).FindSavingCategory(loan)!.DebtBalance);
    }

    [Fact]
    public async Task An_edit_that_omits_the_owner_keeps_the_cross_account_link()
    {
        // ★ The reason UpdateRecurringRequest.LinkedDebtAccountId is not authoritative. An older client re-sends
        // the bucket id alone on every unrelated edit; read plainly as "this account", the link would break and the
        // next post would book a silent lump.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_keep");
        var billId = await LinkBill(client, payer, owner, loan, cat, fund);

        (await client.PutAsJsonAsync($"/accounts/{payer}/recurring/{billId}",
            new UpdateRecurringRequest("Mortgage (renamed)", "fixed", 600m, 10, cat, fund, null, false, loan)))
            .EnsureSuccessStatusCode();

        var bill = (await Snapshot(client, payer)).FindRecurring(billId)!;
        Assert.Equal(owner, bill.LinkedDebtAccountId);
        Assert.Equal("Mortgage (renamed)", bill.Name);
    }

    [Fact]
    public async Task Moving_the_bill_onto_a_loan_in_this_account_clears_the_foreign_owner()
    {
        // The other half of the rule: a DIFFERENT bucket id restates the pair, so the incoming owner (here, none)
        // applies. Bucket and owner are one fact.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_move");
        var billId = await LinkBill(client, payer, owner, loan, cat, fund);
        var ownLoan = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 5m, DebtInstallment: 200m)));

        (await client.PutAsJsonAsync($"/accounts/{payer}/recurring/{billId}",
            new UpdateRecurringRequest("Mortgage", "fixed", 600m, 10, cat, fund, null, false, ownLoan)))
            .EnsureSuccessStatusCode();

        var bill = (await Snapshot(client, payer)).FindRecurring(billId)!;
        Assert.Equal(ownLoan, bill.LinkedDebtBucketId);
        Assert.Null(bill.LinkedDebtAccountId);
        Assert.False(bill.IsCrossAccountInstallment);
    }

    [Fact]
    public async Task A_loan_in_an_account_the_caller_cannot_read_is_not_found()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xdebt_auth_a");
        var payer = await CreateAccount(client, "Mine");
        var (cat, fund) = await SeedAsync(client, payer.Id, auth.UserId, "Loan");

        var (other, otherAuth) = await _factory.RegisterAndAuthAsync("xdebt_auth_b");
        var stranger = await CreateAccount(other, "Theirs");
        await SeedAsync(other, stranger.Id, otherAuth.UserId, "Housing");
        var strangerLoan = await IdOf(await other.PostAsJsonAsync($"/accounts/{stranger.Id}/savings/buckets",
            new SaveSavingBucketRequest("Mortgage", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));

        var response = await client.PostAsJsonAsync($"/accounts/{payer.Id}/recurring",
            new AddRecurringRequest("Mortgage", "expense", "fixed", 600m, 10, cat, fund,
                LinkedDebtBucketId: strangerLoan, LinkedDebtAccountId: stranger.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);   // 404, never 403 — no existence leak
    }

    [Fact]
    public async Task A_currency_mismatch_is_refused()
    {
        // The rows are posted in the bill's currency and the principal comes off a balance in the loan's, so a
        // mismatch would move a figure by a number that means something else.
        var (client, auth) = await _factory.RegisterAndAuthAsync("xdebt_ccy");
        var payer = await CreateAccount(client, "Mine");
        var owner = await CreateAccount(client, "Joint", "BGN");
        var (cat, fund) = await SeedAsync(client, payer.Id, auth.UserId, "Loan");
        await SeedAsync(client, owner.Id, auth.UserId, "Housing", "BGN");
        var loan = await IdOf(await client.PostAsJsonAsync($"/accounts/{owner.Id}/savings/buckets",
            new SaveSavingBucketRequest("Mortgage", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));

        var response = await client.PostAsJsonAsync($"/accounts/{payer.Id}/recurring",
            new AddRecurringRequest("Mortgage", "expense", "fixed", 600m, 10, cat, fund,
                LinkedDebtBucketId: loan, LinkedDebtAccountId: owner.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_bucket_that_is_not_in_the_named_account_is_rejected()
    {
        var (client, payer, owner, _, cat, fund) = await TwoAccountsAsync("xdebt_badbucket");

        var response = await client.PostAsJsonAsync($"/accounts/{payer}/recurring",
            new AddRecurringRequest("Mortgage", "expense", "fixed", 600m, 10, cat, fund,
                LinkedDebtBucketId: Guid.NewGuid(), LinkedDebtAccountId: owner));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_bill_whose_loan_has_gone_posts_a_lump_and_says_so()
    {
        // ⚠️ The degrade path, and the reason it is reported rather than swallowed: the PAYMENT is still recorded,
        // as one expense, while the loan's balance does not move. Silent, that surfaces a quarter later.
        var (client, payer, owner, loan, cat, fund) = await TwoAccountsAsync("xdebt_gone");
        var billId = await LinkBill(client, payer, owner, loan, cat, fund);
        (await client.DeleteAsync($"/accounts/{owner}/savings/buckets/{loan}")).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync($"/accounts/{payer}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(600m));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<RecurringMutationDto>())!;

        Assert.True(result.LoanUnreachable);
        var row = Assert.Single((await Snapshot(client, payer)).CurrentPeriod!.Expenses);
        Assert.Null(row.Part);              // one plain expense, not a split
        Assert.Equal(600m, row.Amount.Amount);   // the money is not lost
    }

    [Fact]
    public async Task An_ordinary_same_account_bill_is_unchanged()
    {
        // The regression pin: every new field absent must leave the single-account path exactly as it was.
        var (client, auth) = await _factory.RegisterAndAuthAsync("xdebt_plain");
        var account = await CreateAccount(client, "Mine");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId, "Loan");
        var loan = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));
        var billId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 600m, 10, cat, fund, LinkedDebtBucketId: loan)));

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(600m));
        resp.EnsureSuccessStatusCode();
        Assert.False((await resp.Content.ReadFromJsonAsync<RecurringMutationDto>())!.LoanUnreachable);

        var agg = await Snapshot(client, account.Id);
        Assert.Null(agg.FindRecurring(billId)!.LinkedDebtAccountId);
        Assert.All(agg.CurrentPeriod!.Expenses, r => Assert.Null(r.DebtBucketAccountId));
        Assert.Equal(19500m, agg.FindSavingCategory(loan)!.DebtBalance);
    }
}
