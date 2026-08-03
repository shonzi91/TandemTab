using FinApp.Domain.Savings;
using FinApp.Forecasting;

namespace FinApp.Domain.Tests;

/// <summary>
/// The R1 "informative debt" read-outs: interest paid so far and interest still to pay, plus the input flexibility
/// (enter current owed OR original + already-paid principal) and the optional origination date that makes paid-interest
/// exact rather than estimated. All projection-only — nothing here touches the money model.
/// </summary>
public class DebtInterestTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    private static SavingCategory Loan(decimal balance = 20_000m, decimal rate = 6m, decimal installment = 400m,
                                       decimal? original = null, DateOnly? asOf = null)
    {
        var s = new SavingCategory("Car loan");
        s.ConfigureDebt(balance, rate, installment, originalBalance: original ?? balance, balanceAsOf: asOf ?? Jan);
        return s;
    }

    [Fact]
    public void Remaining_interest_is_what_is_left_to_pay_from_here()
    {
        var loan = Loan();
        var rem = loan.RemainingInterest(Jan);
        Assert.True(rem > 0m);
        // A year of payments later there's less interest still to come.
        Assert.True(loan.RemainingInterest(Jan.AddMonths(12)) < rem);
    }

    [Fact]
    public void Remaining_interest_is_zero_when_the_installment_cannot_clear_it()
    {
        // €50/mo on €20,000 @ 6% is €100/mo of interest — it never clears, so there's no finite interest to promise.
        var loan = Loan(installment: 50m);
        Assert.Equal(0m, loan.RemainingInterest(Jan));
    }

    [Fact]
    public void Remaining_interest_is_zero_with_no_installment()
    {
        var loan = Loan(installment: 0m);
        Assert.Equal(0m, loan.RemainingInterest(Jan));
    }

    [Fact]
    public void Paid_interest_is_reconstructed_from_the_schedule()
    {
        // Started in Jan, read a year on: paid interest is the interest portion the amortization schedule accrues over
        // those 12 installments — NOT total-paid minus a typed balance.
        var loan = new SavingCategory("Car loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, originalBalance: 20_000m, balanceAsOf: Jan, startDate: Jan);
        var asOf = Jan.AddMonths(12);
        var expected = LoanForecast.InterestAccrued(20_000m, 6m, 400m, 12);

        Assert.Equal(expected, loan.PaidInterestSoFar(asOf));
        Assert.True(loan.PaidInterestSoFar(asOf) > 0m);
    }

    [Fact]
    public void Paid_interest_ignores_an_inconsistent_typed_balance()
    {
        // The bug the user hit: original €25k, "already paid" typed as €10k (→ €15k owed), started 26 months ago. The
        // old formula did total-paid (€400×26 = €10,400) − typed principal (€10k) = a nonsensical €400. The schedule
        // reconstruction ignores the typed balance, so ~26 months of a €25k loan at 6% accrues real interest (>€2,500).
        var start = new DateOnly(2024, 6, 1);
        var asOf = new DateOnly(2026, 8, 1);   // 26 months on
        var loan = new SavingCategory("Car loan");
        loan.ConfigureDebt(15_000m, 6m, 400m, originalBalance: 25_000m, balanceAsOf: asOf, startDate: start);

        var paid = loan.PaidInterestSoFar(asOf);
        Assert.Equal(LoanForecast.InterestAccrued(25_000m, 6m, 400m, 26), paid);
        Assert.NotEqual(400m, paid);          // not the old bogus figure
        Assert.True(paid > 2_500m);           // real interest on a €25k loan run 26 months
    }

    [Fact]
    public void Paid_interest_is_estimated_when_no_start_date()
    {
        // €25k original, €20k owed now, no origination date: infer the elapsed months by amortizing €25k → €20k, then
        // accrue interest over that span. Consistent, and flagged as the rougher (inferred-timeline) estimate.
        var loan = Loan(balance: 20_000m, original: 25_000m);
        Assert.True(loan.DebtPaidInterestIsEstimate);
        Assert.True(loan.PaidInterestSoFar(Jan) > 0m);
    }

    [Fact]
    public void Paid_interest_is_zero_before_anything_is_paid()
    {
        // Original equals current and the loan just started — nothing paid, no interest yet.
        var loan = new SavingCategory("Fresh loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, originalBalance: 20_000m, balanceAsOf: Jan, startDate: Jan);
        Assert.Equal(0m, loan.PaidInterestSoFar(Jan));
    }

    [Fact]
    public void Original_balance_can_exceed_current_and_is_kept()
    {
        // The "original + already-paid principal" input mode: the client passes original 25k and current 20k. The
        // never-drops guard must not bump original DOWN to current — 25k stays, and €5k reads as paid off.
        var loan = Loan(balance: 20_000m, original: 25_000m);
        Assert.Equal(25_000m, loan.DebtOriginalBalance);
        Assert.Equal(5_000m, loan.DebtPaidOff);
    }

    [Fact]
    public void Entering_current_or_original_plus_paid_describe_the_same_loan()
    {
        // Mode A: "I owe €20k" (original defaults to current). Mode B: "I borrowed €25k, paid €5k principal" → the
        // client computes current €20k and passes original €25k. Same balance either way; only the baseline differs.
        var modeA = Loan(balance: 20_000m);                        // original defaults to 20k
        var modeB = Loan(balance: 20_000m, original: 25_000m);     // original stated as 25k
        Assert.Equal(modeA.DebtBalanceOn(Jan), modeB.DebtBalanceOn(Jan));
        Assert.Equal(modeA.RemainingInterest(Jan), modeB.RemainingInterest(Jan));
    }

    [Fact]
    public void Setting_a_start_date_makes_paid_interest_exact()
    {
        var loan = Loan();
        Assert.True(loan.DebtPaidInterestIsEstimate);
        loan.SetDebtStartDate(Jan.AddMonths(-6));
        Assert.False(loan.DebtPaidInterestIsEstimate);
    }

    [Fact]
    public void Installment_due_day_is_stored_and_clearable()
    {
        var loan = Loan();
        loan.SetDebtInstallmentDay(15);
        Assert.Equal(15, loan.DebtInstallmentDay);
        loan.SetDebtInstallmentDay(null);
        Assert.Null(loan.DebtInstallmentDay);
    }

    [Fact]
    public void Installment_due_day_must_be_within_the_month()
    {
        var loan = Loan();
        Assert.Throws<ArgumentOutOfRangeException>(() => loan.SetDebtInstallmentDay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => loan.SetDebtInstallmentDay(32));
    }

    [Fact]
    public void Clearing_the_debt_drops_the_new_fields()
    {
        var loan = Loan();
        loan.SetDebtInstallmentDay(15);
        loan.SetDebtStartDate(Jan);
        loan.ClearDebt();
        Assert.Null(loan.DebtInstallmentDay);
        Assert.Null(loan.DebtStartDate);
    }
}
