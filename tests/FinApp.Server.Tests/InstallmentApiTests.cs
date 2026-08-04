using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The R2 installment endpoints (POST/DELETE /accounts/{id}/installments): one payment posts several linked rows,
/// removing it takes them all, and a payment-driven loan's balance moves by the principal only. Each write is
/// confirmed through the /savings and /spending reads, so the command and the computed read prove each other.
/// </summary>
public class InstallmentApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public InstallmentApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 15);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed an account holding a €20,000 @ 6% loan on a €400 installment (so a month's interest is exactly
    /// €100), plus the categories the split rows are filed under.</summary>
    private static async Task<(Guid Loan, Guid LoanCat, Guid InsCat, Guid Fund)> SeedAsync(
        HttpClient client, Guid accountId, Guid memberId, bool paymentDriven)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var loanCat = agg.AddCategory("Loan").Id;
        var insCat = agg.AddCategory("Insurance").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var bucket = agg.AddSavingCategory("Car loan");
        agg.ConfigureSavingDebt(bucket.Id, 20_000m, 6m, 400m, balanceAsOf: new DateOnly(2026, 1, 1));
        if (paymentDriven) agg.SetSavingDebtPaymentDriven(bucket.Id, true, new DateOnly(2026, 1, 1));
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(5_000m, "EUR"), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (bucket.Id, loanCat, insCat, fund);
    }

    private static Task<SavingsViewDto?> Savings(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{accountId}/savings");

    [Fact]
    public async Task Logging_an_installment_posts_a_linked_row_per_part()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_post");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, insCat, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/installments",
            new LogInstallmentRequest(loan, 460m, fund, When, loanCat, loanCat,
                Additional: [new InstallmentExtraDto(60m, insCat)]));
        resp.EnsureSuccessStatusCode();
        var delta = (await resp.Content.ReadFromJsonAsync<InstallmentMutationDto>())!;

        Assert.Equal(3, delta.Rows.Count);
        Assert.All(delta.Rows, r => Assert.Equal(delta.GroupId, r.InstallmentGroupId));
        Assert.All(delta.Rows, r => Assert.Equal(loan, r.DebtBucketId));
        Assert.Equal(100m, delta.Rows.Single(r => r.InstallmentPart == "interest").Amount);
        Assert.Equal(300m, delta.Rows.Single(r => r.InstallmentPart == "principal").Amount);
        Assert.Equal(60m, delta.Rows.Single(r => r.InstallmentPart == "additional").Amount);

        // The whole payment left the account once — the split is categorization, not extra spending.
        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.Equal(460m, spending.Expenses.Sum(e => e.Amount));
    }

    [Fact]
    public async Task A_payment_driven_loan_drops_by_the_principal_only()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_driven");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, _, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: true);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/installments",
            new LogInstallmentRequest(loan, 400m, fund, When, loanCat, loanCat))).EnsureSuccessStatusCode();

        var savings = await Savings(client, account.Id);
        var bucket = savings!.Buckets.Single(b => b.Id == loan);
        Assert.Equal(19_700m, bucket.DebtBalance);
        Assert.True(bucket.DebtPaymentDriven);
    }

    [Fact]
    public async Task A_schedule_driven_loan_is_not_advanced_twice_by_a_logged_installment()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_sched");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, _, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);
        var before = (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan).DebtBalance;

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/installments",
            new LogInstallmentRequest(loan, 400m, fund, When, loanCat, loanCat))).EnsureSuccessStatusCode();

        var after = (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan).DebtBalance;
        Assert.Equal(before, after);   // the schedule already accounted for this month
    }

    [Fact]
    public async Task Removing_an_installment_takes_every_row_and_restores_a_payment_driven_balance()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_del");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, insCat, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: true);
        var delta = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/installments",
            new LogInstallmentRequest(loan, 460m, fund, When, loanCat, loanCat,
                Additional: [new InstallmentExtraDto(60m, insCat)])))
            .Content.ReadFromJsonAsync<InstallmentMutationDto>())!;

        (await client.DeleteAsync($"/accounts/{account.Id}/installments/{delta.GroupId}")).EnsureSuccessStatusCode();

        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.Empty(spending.Expenses);
        Assert.Equal(20_000m, (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan).DebtBalance);
    }

    [Fact]
    public async Task Extra_lines_over_the_payment_are_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_over");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, insCat, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/installments",
            new LogInstallmentRequest(loan, 100m, fund, When, loanCat, loanCat,
                Additional: [new InstallmentExtraDto(150m, insCat)]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);   // domain: the lines exceed what was paid
    }

    [Fact]
    public async Task Confirming_a_debt_linked_bill_posts_a_split_installment()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_rec");
        var account = await CreateAccount(client, "Home");
        var (loan, loanCat, _, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: true);

        var billId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 400m, 15, loanCat, fund,
                LinkedDebtBucketId: loan)))
            .Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(400m))).EnsureSuccessStatusCode();

        var spending = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.Equal(2, spending.Expenses.Count);
        Assert.Equal(100m, spending.Expenses.Single(e => e.InstallmentPart == "interest").Amount);
        Assert.Equal(300m, spending.Expenses.Single(e => e.InstallmentPart == "principal").Amount);
        // …and the loan moved by the principal, because posting the bill IS logging the installment.
        Assert.Equal(19_700m, (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan).DebtBalance);
    }

    [Fact]
    public async Task An_unlinked_bill_still_posts_one_plain_expense()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_rec_plain");
        var account = await CreateAccount(client, "Home");
        var (_, loanCat, _, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);

        var billId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Gym", "expense", "fixed", 40m, 15, loanCat, fund)))
            .Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{billId}/confirm",
            new ConfirmRecurringRequest(40m))).EnsureSuccessStatusCode();

        var expense = Assert.Single((await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!.Expenses);
        Assert.Null(expense.InstallmentPart);
        Assert.Equal(40m, expense.Amount);
    }

    [Fact]
    public async Task A_bill_cannot_be_linked_to_something_that_is_not_a_debt()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_rec_bad");
        var account = await CreateAccount(client, "Home");
        var (_, loanCat, _, fund) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 400m, 15, loanCat, fund,
                LinkedDebtBucketId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Saving_the_bucket_can_flip_payment_driving_without_moving_the_balance()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("inst_flip");
        var account = await CreateAccount(client, "Home");
        var (loan, _, _, _) = await SeedAsync(client, account.Id, auth.UserId, paymentDriven: false);
        var owed = (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan).DebtBalance;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{loan}",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: owed!.Value, DebtRate: 6m,
                DebtInstallment: 400m, DebtPaymentDriven: true))).EnsureSuccessStatusCode();

        var after = (await Savings(client, account.Id))!.Buckets.Single(b => b.Id == loan);
        Assert.True(after.DebtPaymentDriven);
        Assert.Equal(owed, after.DebtBalance);   // flipping the switch reports the same figure, it just stops moving
    }
}
