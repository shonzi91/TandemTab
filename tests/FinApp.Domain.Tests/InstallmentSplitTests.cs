using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;
using FinApp.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// R2 "installment split": logging a loan payment as linked principal/interest/extra rows, and the hybrid balance —
/// a payment-driven debt moves only when a payment is logged, a schedule-driven one keeps walking its own schedule.
/// </summary>
public class InstallmentSplitTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan15 = new(2026, 1, 15);

    private static Period Setup(out Account account, out Guid fund, out Guid loanCat, out Guid extraCat, out SavingCategory loan,
                                decimal balance = 20_000m, decimal rate = 6m, decimal installment = 400m, bool paymentDriven = false)
    {
        account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.AddCategory("Loan");
        account.AddCategory("Insurance");
        fund = account.FundId("Bank");
        loanCat = account.Categories.First(c => c.Name == "Loan").Id;
        extraCat = account.Categories.First(c => c.Name == "Insurance").Id;

        var bucket = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(bucket.Id, balance, rate, installment, balanceAsOf: Jan1);
        if (paymentDriven) account.SetSavingDebtPaymentDriven(bucket.Id, true, Jan1);
        loan = account.FindSavingCategory(bucket.Id)!;

        var period = account.StartPeriod(Jan1, new DateOnly(2026, 1, 31));
        period.SetInitialBalance(fund, M(5_000m));
        return period;
    }

    // --- The split ------------------------------------------------------------------------------------

    [Fact]
    public void Installment_splits_into_interest_and_principal_off_the_loan_schedule()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);

        var rows = period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        // €20,000 at 6% APR is €100 of interest in the month; the rest of the €400 clears principal.
        var expectedInterest = LoanForecast.MonthlyInterest(20_000m, 6m);
        Assert.Equal(100m, expectedInterest);
        Assert.Equal(M(100m), rows.Single(r => r.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(M(300m), rows.Single(r => r.Part == InstallmentPart.Principal).Amount);
        // The rows sum to exactly what was paid — the ledger reconciles to reality, not to the schedule.
        Assert.Equal(M(400m), rows.Aggregate(M(0m), (acc, r) => acc + r.Amount));
    }

    [Fact]
    public void Extra_lines_come_off_the_top_and_only_the_rest_is_split()
    {
        var period = Setup(out _, out var fund, out var loanCat, out var extraCat, out var loan);

        var rows = period.LogInstallment(loan, M(460m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            additional: [new InstallmentExtra(M(60m), extraCat)]);

        Assert.Equal(M(60m), rows.Single(r => r.Part == InstallmentPart.Additional).Amount);
        Assert.Equal(M(100m), rows.Single(r => r.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(M(300m), rows.Single(r => r.Part == InstallmentPart.Principal).Amount);
        Assert.Equal(M(460m), rows.Aggregate(M(0m), (acc, r) => acc + r.Amount));
    }

    [Fact]
    public void Several_extra_lines_stay_separate_rows_with_their_own_categories_and_tags()
    {
        // The whole reason extras are a list: insurance and tax must keep their own Breakdown slices.
        var period = Setup(out var account, out var fund, out var loanCat, out var extraCat, out var loan);
        var tax = account.AddCategory("Tax").Id;
        var tagId = account.AddTag("Insurance").Id;

        var rows = period.LogInstallment(loan, M(490m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            additional: [new InstallmentExtra(M(60m), extraCat, tagId), new InstallmentExtra(M(30m), tax)]);

        var extras = rows.Where(r => r.Part == InstallmentPart.Additional).ToList();
        Assert.Equal(2, extras.Count);
        Assert.Equal(tagId, extras.Single(r => r.CategoryId == extraCat).TagId);
        Assert.Null(extras.Single(r => r.CategoryId == tax).TagId);
        Assert.Equal(M(490m), rows.Aggregate(M(0m), (acc, r) => acc + r.Amount));
    }

    [Fact]
    public void Every_row_shares_one_group_and_points_at_the_loan()
    {
        var period = Setup(out _, out var fund, out var loanCat, out var extraCat, out var loan);

        var rows = period.LogInstallment(loan, M(460m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            additional: [new InstallmentExtra(M(60m), extraCat)]);

        var groupId = rows[0].InstallmentGroupId;
        Assert.NotNull(groupId);
        Assert.All(rows, r => Assert.Equal(groupId, r.InstallmentGroupId));
        Assert.All(rows, r => Assert.Equal(loan.Id, r.DebtBucketId));
        Assert.Equal(3, period.InstallmentGroup(groupId!.Value).Count());
    }

    [Fact]
    public void An_underpayment_books_everything_as_interest_and_clears_no_principal()
    {
        // €60 against €100 of monthly interest: honest reporting beats a negative principal row.
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);

        var rows = period.LogInstallment(loan, M(60m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        Assert.Equal(M(60m), rows.Single().Amount);
        Assert.Equal(InstallmentPart.Interest, rows.Single().Part);
    }

    [Fact]
    public void A_zero_rate_loan_posts_no_interest_row()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan, rate: 0m);

        var rows = period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        Assert.Equal(InstallmentPart.Principal, rows.Single().Part);
        Assert.Equal(M(400m), rows.Single().Amount);
    }

    [Fact]
    public void Extra_lines_cannot_exceed_the_payment()
    {
        var period = Setup(out _, out var fund, out var loanCat, out var extraCat, out var loan);

        Assert.Throws<InvalidOperationException>(() =>
            period.LogInstallment(loan, M(100m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
                additional: [new InstallmentExtra(M(150m), extraCat)]));
    }

    [Fact]
    public void Only_a_debt_bucket_takes_an_installment()
    {
        var period = Setup(out var account, out var fund, out var loanCat, out _, out _);
        var goal = account.AddSavingCategory("Holiday");

        Assert.Throws<InvalidOperationException>(() =>
            period.LogInstallment(goal, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat));
    }

    [Fact]
    public void The_whole_payment_leaves_the_account_once()
    {
        // The split is categorization, not extra spending: closing falls by the payment, not by a multiple of it.
        var period = Setup(out _, out var fund, out var loanCat, out var extraCat, out var loan);
        var before = period.ExpectedClosingBalance;

        period.LogInstallment(loan, M(460m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            additional: [new InstallmentExtra(M(60m), extraCat)]);

        Assert.Equal(before - M(460m), period.ExpectedClosingBalance);
        Assert.Equal(M(460m), period.ExpensesTotal);
    }

    // --- The hybrid balance ---------------------------------------------------------------------------

    [Fact]
    public void A_payment_driven_debt_drops_by_the_principal_only()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan, paymentDriven: true);

        period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        // €300 of principal came off; the €100 of interest bought nothing and clears no debt.
        Assert.Equal(19_700m, loan.DebtBalanceOn(Jan15));
    }

    [Fact]
    public void A_payment_driven_debt_does_not_advance_on_its_own()
    {
        // The trade the user makes by switching it on: an unlogged month leaves the balance where it was.
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan, paymentDriven: true);
        period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        Assert.Equal(19_700m, loan.DebtBalanceOn(Jan15.AddMonths(6)));
    }

    [Fact]
    public void A_schedule_driven_debt_keeps_walking_and_a_logged_installment_does_not_double_advance_it()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);
        var walkedSixMonths = loan.DebtBalanceOn(Jan1.AddMonths(6));

        period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);

        // Unchanged: the schedule already accounts for this month's installment.
        Assert.Equal(walkedSixMonths, loan.DebtBalanceOn(Jan1.AddMonths(6)));
    }

    [Fact]
    public void Switching_on_payment_driving_snapshots_what_is_owed_today()
    {
        // Freezing a stale anchored balance would silently reverse months of repayment.
        var loan = new SavingCategory("Car loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, balanceAsOf: Jan1);
        var owedInJune = loan.DebtBalanceOn(Jan1.AddMonths(6));
        Assert.True(owedInJune < 20_000m);

        loan.SetPaymentDriven(true, Jan1.AddMonths(6));

        Assert.Equal(owedInJune, loan.DebtBalance);
        Assert.Equal(owedInJune, loan.DebtBalanceOn(Jan1.AddMonths(6)));
        Assert.Equal(Jan1.AddMonths(6), loan.DebtBalanceAsOf);
    }

    [Fact]
    public void Switching_off_payment_driving_re_anchors_so_the_schedule_does_not_re_walk_paid_months()
    {
        var loan = new SavingCategory("Car loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, balanceAsOf: Jan1);
        loan.SetPaymentDriven(true, Jan1);
        loan.RecordDebtPayment(300m, Jan15);          // one logged installment's principal
        var owed = loan.DebtBalanceOn(Jan15);

        loan.SetPaymentDriven(false, Jan15);

        // Same figure the moment it flips, and the schedule restarts from that day — not from January's anchor.
        Assert.Equal(owed, loan.DebtBalanceOn(Jan15));
        Assert.Equal(Jan15, loan.DebtBalanceAsOf);
    }

    [Fact]
    public void Re_stating_the_same_mode_does_not_re_date_the_loan()
    {
        var loan = new SavingCategory("Car loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, balanceAsOf: Jan1);

        loan.SetPaymentDriven(false, Jan1.AddMonths(6));   // already schedule-driven

        Assert.Equal(Jan1, loan.DebtBalanceAsOf);
        Assert.Equal(20_000m, loan.DebtBalance);
    }

    // --- Removing a group -----------------------------------------------------------------------------

    [Fact]
    public void Removing_an_installment_removes_every_row_of_it()
    {
        var period = Setup(out _, out var fund, out var loanCat, out var extraCat, out var loan);
        var rows = period.LogInstallment(loan, M(460m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            additional: [new InstallmentExtra(M(60m), extraCat)]);
        var groupId = rows[0].InstallmentGroupId!.Value;

        period.RemoveInstallmentGroup(groupId, loan);

        Assert.Empty(period.Expenses);
        Assert.Equal(M(0m), period.ExpensesTotal);
    }

    [Fact]
    public void Removing_an_installment_puts_the_principal_back_on_a_payment_driven_debt()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan, paymentDriven: true);
        var rows = period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);
        Assert.Equal(19_700m, loan.DebtBalanceOn(Jan15));

        period.RemoveInstallmentGroup(rows[0].InstallmentGroupId!.Value, loan);

        Assert.Equal(20_000m, loan.DebtBalanceOn(Jan15));
    }

    [Fact]
    public void Removing_an_installment_leaves_a_schedule_driven_debt_alone()
    {
        // Nothing was taken off it in the first place — the schedule owns the balance.
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);
        var rows = period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);
        var owed = loan.DebtBalanceOn(Jan15);

        period.RemoveInstallmentGroup(rows[0].InstallmentGroupId!.Value, loan);

        Assert.Equal(owed, loan.DebtBalanceOn(Jan15));
    }

    // --- Recurring bills linked to a loan ---------------------------------------------------------------

    private static RecurringItem Bill(Guid categoryId, Guid fundId, Guid? debtId, decimal amount = 400m)
    {
        var item = new RecurringItem("Car loan", RecurringKind.Expense, RecurringAmountMode.Fixed, amount, 15, categoryId, fundId);
        item.SetLinkedDebtBucket(debtId);
        return item;
    }

    [Fact]
    public void A_linked_bill_posts_a_split_installment_instead_of_one_lump_expense()
    {
        var period = Setup(out var account, out var fund, out var loanCat, out _, out var loan);
        var (principalTag, interestTag) = account.EnsureInstallmentTags(loan.Id, "Loan principal", "Loan interest");
        var bill = Bill(loanCat, fund, loan.Id);

        period.PostRecurring(bill, 400m, Guid.NewGuid(), false, loan, principalTag, interestTag);

        Assert.Equal(2, period.Expenses.Count);
        Assert.Equal(M(100m), period.Expenses.Single(e => e.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(M(300m), period.Expenses.Single(e => e.Part == InstallmentPart.Principal).Amount);
        Assert.Equal(interestTag, period.Expenses.Single(e => e.Part == InstallmentPart.Interest).TagId);
        Assert.Equal(principalTag, period.Expenses.Single(e => e.Part == InstallmentPart.Principal).TagId);
        Assert.All(period.Expenses, e => Assert.Equal("Car loan", e.Note));
    }

    [Fact]
    public void A_linked_bill_is_still_marked_handled_for_the_period()
    {
        // The split path returns early — it must not skip the bookkeeping that stops the bill nagging again.
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);
        var bill = Bill(loanCat, fund, loan.Id);

        period.PostRecurring(bill, 400m, Guid.NewGuid(), false, loan);

        Assert.False(bill.IsPending(period.From));
    }

    [Fact]
    public void An_unlinked_bill_still_posts_one_plain_expense()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);
        var bill = Bill(loanCat, fund, debtId: null);

        period.PostRecurring(bill, 400m, Guid.NewGuid(), false, loan);

        var expense = Assert.Single(period.Expenses);
        Assert.Null(expense.Part);
        Assert.Equal(M(400m), expense.Amount);
    }

    [Fact]
    public void A_linked_bill_falls_back_to_a_plain_expense_when_the_loan_is_gone()
    {
        // Losing the split beats losing the payment: the money left the account either way.
        var period = Setup(out _, out var fund, out var loanCat, out _, out _);
        var bill = Bill(loanCat, fund, Guid.NewGuid());

        period.PostRecurring(bill, 400m, Guid.NewGuid(), false, linkedDebt: null);

        var expense = Assert.Single(period.Expenses);
        Assert.Null(expense.InstallmentGroupId);
        Assert.Equal(M(400m), expense.Amount);
    }

    [Fact]
    public void Income_cannot_be_linked_to_a_loan()
    {
        var salary = new RecurringItem("Salary", RecurringKind.Income, RecurringAmountMode.Fixed, 2000m, 1, Guid.NewGuid(), Guid.NewGuid());

        salary.SetLinkedDebtBucket(Guid.NewGuid());

        Assert.Null(salary.LinkedDebtBucketId);
        Assert.False(salary.IsLoanInstallment);
    }

    [Fact]
    public void Installment_tags_are_reused_across_posts_rather_than_duplicated_per_language()
    {
        // The web passes localized names and the server's auto-post passes English ones. Whatever the loan's rows
        // already carry must win, or one loan slowly grows two "interest" tags and the Breakdown slice lies.
        var period = Setup(out var account, out var fund, out var loanCat, out _, out var loan);
        var (bgPrincipal, bgInterest) = account.EnsureInstallmentTags(loan.Id, "Главница по заем", "Лихва по заем");
        period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat,
            principalTagId: bgPrincipal, interestTagId: bgInterest);

        var (againPrincipal, againInterest) = account.EnsureInstallmentTags(loan.Id, "Loan principal", "Loan interest");

        Assert.Equal(bgPrincipal, againPrincipal);
        Assert.Equal(bgInterest, againInterest);
        Assert.Equal(2, account.Tags.Count);
    }

    [Fact]
    public void Editing_one_row_keeps_it_linked_to_its_installment()
    {
        var period = Setup(out _, out var fund, out var loanCat, out _, out var loan);
        var rows = period.LogInstallment(loan, M(400m), Jan15, Guid.NewGuid(), fund, loanCat, loanCat);
        var interest = rows.Single(r => r.Part == InstallmentPart.Interest);

        var edited = period.EditExpense(interest.Id, loanCat, M(110m), fund, null, Jan15);

        Assert.Equal(interest.InstallmentGroupId, edited.InstallmentGroupId);
        Assert.Equal(InstallmentPart.Interest, edited.Part);
        Assert.Equal(loan.Id, edited.DebtBucketId);
    }
}
