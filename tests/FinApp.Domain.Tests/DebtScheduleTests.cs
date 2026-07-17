using FinApp.Domain.Savings;

namespace FinApp.Domain.Tests;

/// <summary>
/// The debt schedule: a bucket's balance is <b>derived</b> from its terms and how long it's been, not from the app
/// witnessing each installment. That matters because the installment is often paid from another account this snapshot
/// can't see — so the balance has to be right without anyone telling it.
/// </summary>
public class DebtScheduleTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    private static SavingCategory Loan(decimal balance = 20_000m, decimal rate = 6m, decimal installment = 400m, DateOnly? asOf = null)
    {
        var s = new SavingCategory("Car loan");
        s.ConfigureDebt(balance, rate, installment, originalBalance: balance, balanceAsOf: asOf ?? Jan);
        return s;
    }

    [Fact]
    public void An_installment_only_pays_down_what_is_left_after_interest()
    {
        var loan = Loan();
        // Month 1 on €20,000 @ 6%: €100 interest, so only €300 of the €400 hits the principal.
        Assert.Equal(19_700m, loan.DebtBalanceOn(Jan.AddMonths(1)));
    }

    [Fact]
    public void Subtracting_the_whole_installment_would_over_credit()
    {
        var loan = Loan();
        // The bug this design exists to prevent: naive subtraction says €19,600 after one €400 payment.
        var naive = 20_000m - 400m;
        Assert.NotEqual(naive, loan.DebtBalanceOn(Jan.AddMonths(1)));
        Assert.True(loan.DebtBalanceOn(Jan.AddMonths(1)) > naive);
    }

    [Fact]
    public void The_balance_walks_forward_on_its_own_with_no_payment_recorded()
    {
        var loan = Loan();
        var afterAYear = loan.DebtBalanceOn(Jan.AddMonths(12));
        Assert.True(afterAYear < 20_000m);          // it moved without anyone recording anything
        Assert.True(afterAYear > 20_000m - 4_800m); // but not by the full 12 × €400 — interest took its cut
    }

    [Fact]
    public void A_part_month_does_not_count_until_its_day_comes_round()
    {
        var loan = Loan();
        Assert.Equal(20_000m, loan.DebtBalanceOn(new DateOnly(2026, 1, 31)));   // same month, no payment due yet
        Assert.Equal(19_700m, loan.DebtBalanceOn(new DateOnly(2026, 2, 1)));    // the day comes round
    }

    [Fact]
    public void Asking_before_the_anchor_gives_the_anchored_balance()
    {
        var loan = Loan();
        Assert.Equal(20_000m, loan.DebtBalanceOn(Jan.AddMonths(-6)));
    }

    [Fact]
    public void A_bucket_with_no_anchor_keeps_its_stored_balance()
    {
        // Legacy buckets (and anything without a schedule) must behave exactly as they did before this existed.
        var s = new SavingCategory("Old debt");
        s.ConfigureDebt(5_000m, 6m, 200m);   // no balanceAsOf
        Assert.Null(s.DebtBalanceAsOf);
        Assert.Equal(5_000m, s.DebtBalanceOn(Jan.AddMonths(24)));
    }

    [Fact]
    public void A_debt_with_no_installment_never_walks()
    {
        var s = new SavingCategory("Interest-only");
        s.ConfigureDebt(5_000m, 6m, installment: 0m, balanceAsOf: Jan);
        Assert.Equal(5_000m, s.DebtBalanceOn(Jan.AddMonths(12)));
    }

    [Fact]
    public void An_extra_payment_catches_up_to_its_date_then_comes_off_the_principal()
    {
        var loan = Loan();
        // A year of scheduled installments, then €3,000 extra on top on the same day.
        var scheduled = loan.DebtBalanceOn(Jan.AddMonths(12));
        loan.RecordDebtPayment(3_000m, Jan.AddMonths(12));

        Assert.Equal(scheduled - 3_000m, loan.DebtBalance);        // the whole lump hits the principal
        Assert.Equal(Jan.AddMonths(12), loan.DebtBalanceAsOf);     // and re-anchors there
    }

    [Fact]
    public void An_extra_payment_does_not_replay_the_schedule_it_already_absorbed()
    {
        var loan = Loan();
        loan.RecordDebtPayment(3_000m, Jan.AddMonths(12));
        var justAfter = loan.DebtBalanceOn(Jan.AddMonths(12));
        // Reading on the anchor date must return the anchored balance, not walk 12 months a second time.
        Assert.Equal(loan.DebtBalance, justAfter);
    }

    [Fact]
    public void Overpaying_the_whole_balance_clears_it_and_never_goes_negative()
    {
        var loan = Loan();
        loan.RecordDebtPayment(999_999m, Jan);
        Assert.Equal(0m, loan.DebtBalance);
        Assert.True(loan.IsDebtCleared);
    }

    [Fact]
    public void Progress_is_measured_against_the_derived_balance()
    {
        var loan = Loan();
        Assert.Equal(0m, loan.DebtPaidOffOn(Jan));                       // nothing paid on day one
        Assert.Equal(300m, loan.DebtPaidOffOn(Jan.AddMonths(1)));        // one installment's principal
        Assert.Equal(0.015m, loan.DebtProgressRatioOn(Jan.AddMonths(1))); // 300 / 20,000
    }

    [Fact]
    public void Restating_the_balance_re_anchors_it()
    {
        var loan = Loan();
        // The lender says you actually owe €18,000 today — fees, a missed month, whatever. That correction IS the
        // new anchor, and the schedule walks on from there.
        var correctionDay = Jan.AddMonths(6);
        loan.ConfigureDebt(18_000m, 6m, 400m, balanceAsOf: correctionDay);

        Assert.Equal(correctionDay, loan.DebtBalanceAsOf);
        Assert.Equal(18_000m, loan.DebtBalanceOn(correctionDay));
        Assert.Equal(17_690m, loan.DebtBalanceOn(correctionDay.AddMonths(1)));   // €90 interest, €310 principal
    }

    [Fact]
    public void Switching_a_debt_to_another_kind_drops_the_anchor()
    {
        var loan = Loan();
        loan.ClearDebt();
        Assert.Null(loan.DebtBalanceAsOf);
    }
}
