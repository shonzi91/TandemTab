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
/// Item C: a recurring bill bigger than the loan's contractual installment. The owner's real case is a €700 direct
/// debit against a €600 installment, where the extra €100 is health + property insurance the bank bundled into the
/// same mandate — so it is NOT loan money, and must not quietly become principal.
/// <para>These test <see cref="Period.PostRecurring"/>'s routing (which installment the loan is serviced at, and
/// what becomes of the rest). <see cref="InstallmentSplitTests"/> tests <see cref="Period.LogInstallment"/> itself.
/// </para>
/// </summary>
public class RecurringExcessLineTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan31 = new(2026, 1, 31);

    /// <param name="installment">The loan's contractual installment — the figure servicing is capped at.</param>
    private static Period Setup(out Account account, out Guid fund, out Guid billCat, out Guid insuranceCat,
                                out SavingCategory loan, decimal installment = 600m, bool paymentDriven = false)
    {
        account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.AddCategory("Loan");
        account.AddCategory("Insurance");
        fund = account.FundId("Bank");
        billCat = account.Categories.First(c => c.Name == "Loan").Id;
        insuranceCat = account.Categories.First(c => c.Name == "Insurance").Id;

        var bucket = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(bucket.Id, 20_000m, 6m, installment, balanceAsOf: Jan1);
        if (paymentDriven) account.SetSavingDebtPaymentDriven(bucket.Id, true, Jan1);
        loan = account.FindSavingCategory(bucket.Id)!;

        var period = account.StartPeriod(Jan1, Jan31);
        period.SetInitialBalance(fund, M(5_000m));
        return period;
    }

    private static RecurringItem Bill(Guid billCat, Guid fund, Guid loanId, decimal expected = 700m,
                                      Guid? excessCat = null, string? excessLabel = null)
    {
        var item = new RecurringItem("Car loan", RecurringKind.Expense, RecurringAmountMode.Fixed, expected, 15, billCat, fund);
        item.SetLinkedDebtBucket(loanId);
        item.SetExcess(excessCat, excessLabel);
        return item;
    }

    // €20,000 at 6% APR is €100 of interest in the month, whatever is paid on top.
    private const decimal ExpectedInterest = 100m;

    [Fact]
    public void Excess_with_a_category_posts_a_third_row_and_caps_servicing_at_the_installment()
    {
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance, excessLabel: "Health + property");

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        var rows = period.Expenses.ToList();
        Assert.Equal(3, rows.Count);

        var extra = rows.Single(r => r.Part == InstallmentPart.Additional);
        Assert.Equal(M(100m), extra.Amount);
        Assert.Equal(insurance, extra.CategoryId);         // its own category, not the bill's
        Assert.Equal("Health + property", extra.Note);      // named, so the ledger row says what it is

        // The loan was serviced at exactly the contractual €600 — interest off the balance, the rest principal.
        Assert.Equal(ExpectedInterest, LoanForecast.MonthlyInterest(20_000m, 6m));
        Assert.Equal(M(ExpectedInterest), rows.Single(r => r.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(M(500m), rows.Single(r => r.Part == InstallmentPart.Principal).Amount);

        // And the ledger still reconciles to what actually left the account.
        Assert.Equal(M(700m), rows.Aggregate(M(0m), (acc, r) => acc + r.Amount));
    }

    [Fact]
    public void Excess_without_a_category_behaves_exactly_as_before()
    {
        // ★ The regression pin for the deliberate decision that silence must not change money: an existing
        // €700-on-€600 bill must not sprout a new row and drop its principal on the first post after deploy.
        var period = Setup(out _, out var fund, out var billCat, out _, out var loan);
        var item = Bill(billCat, fund, loan.Id);   // no excess category configured

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        var rows = period.Expenses.ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Part == InstallmentPart.Additional);
        Assert.Equal(M(ExpectedInterest), rows.Single(r => r.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(M(600m), rows.Single(r => r.Part == InstallmentPart.Principal).Amount);
    }

    [Fact]
    public void A_label_is_optional_and_falls_back_to_the_bills_own_name()
    {
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance);   // no label

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        Assert.Equal("Car loan", period.Expenses.Single(r => r.Part == InstallmentPart.Additional).Note);
    }

    [Fact]
    public void Paying_exactly_the_installment_posts_no_extra_row()
    {
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan);
        var item = Bill(billCat, fund, loan.Id, expected: 600m, excessCat: insurance);

        period.PostRecurring(item, 600m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        Assert.Equal(2, period.Expenses.Count);
        Assert.DoesNotContain(period.Expenses, r => r.Part == InstallmentPart.Additional);
    }

    [Fact]
    public void Paying_under_the_installment_books_all_of_it_as_interest_and_no_extra_row()
    {
        // Guards the negative-excess clamp: an under-payment has no excess to carve out, and LogInstallment's
        // existing rule (interest can't exceed what was paid) is what should apply.
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance);

        period.PostRecurring(item, 80m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        var row = Assert.Single(period.Expenses);
        Assert.Equal(InstallmentPart.Interest, row.Part);
        Assert.Equal(M(80m), row.Amount);
    }

    [Fact]
    public void A_loan_with_no_stated_installment_falls_back_to_the_old_split()
    {
        // Payment-driven loans often state no installment; there is no contractual figure to cap servicing at,
        // so the whole payment services the loan exactly as it did before.
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan,
            installment: 0m, paymentDriven: true);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance);

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        Assert.Equal(2, period.Expenses.Count);
        Assert.DoesNotContain(period.Expenses, r => r.Part == InstallmentPart.Additional);
    }

    [Fact]
    public void A_payment_driven_balance_moves_by_the_capped_principal_not_the_whole_payment()
    {
        // ★ The behaviour change, stated as a number. Before Item C a €700 bill on a €600 loan moved the balance
        // by ~€600 of principal instead of ~€500 — money that was really insurance, silently paying down a loan.
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan,
            paymentDriven: true);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance);

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        Assert.Equal(20_000m - 500m, loan.DebtBalance);
    }

    [Fact]
    public void The_excess_is_never_banked_as_an_extra_repayment()
    {
        // It is not loan money at all, so it cannot earn the "you're ahead of schedule" credit — and with servicing
        // capped at the installment, the principal that IS paid is the scheduled principal, so nothing is ahead.
        var period = Setup(out _, out var fund, out var billCat, out var insurance, out var loan,
            paymentDriven: true);
        var item = Bill(billCat, fund, loan.Id, excessCat: insurance);

        period.PostRecurring(item, 700m, Guid.NewGuid(), fundSynced: false, linkedDebt: loan);

        Assert.Equal(0m, loan.DebtExtraPrincipalRepaid);
        Assert.Null(loan.AheadOfScheduleOn(Jan31));
    }

    [Fact]
    public void SetExcess_self_clears_on_a_bill_that_services_no_loan()
    {
        var item = new RecurringItem("Netflix", RecurringKind.Expense, RecurringAmountMode.Fixed, 15m, 5, Guid.NewGuid(), Guid.NewGuid());
        item.SetExcess(Guid.NewGuid(), "Insurance");

        Assert.Null(item.ExcessCategoryId);
        Assert.Null(item.ExcessLabel);
    }

    [Fact]
    public void ExcessOn_is_reckoned_from_the_amount_being_paid_not_the_expected_one()
    {
        // The installment is a fact about the loan and the amount is a fact about what left the account. Editing
        // the figure at confirm time flexes the EXCESS; the loan is still serviced at its contractual installment.
        var insurance = Guid.NewGuid();
        var item = new RecurringItem("Car loan", RecurringKind.Expense, RecurringAmountMode.Fixed, 700m, 15, Guid.NewGuid(), Guid.NewGuid());
        item.SetLinkedDebtBucket(Guid.NewGuid());
        item.SetExcess(insurance, null);

        Assert.Equal(100m, item.ExcessOn(700m, 600m));
        Assert.Equal(150m, item.ExcessOn(750m, 600m));   // premium went up; the loan part did not
        Assert.Equal(0m, item.ExcessOn(550m, 600m));     // under-payment: nothing to carve out
        Assert.Equal(0m, item.ExcessOn(700m, 0m));       // no contractual figure to cap at
    }
}
