using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Savings;
using FinApp.Forecasting;

namespace FinApp.Domain.Tests;

/// <summary>
/// Reading a debt <b>backwards</b>. Every balance walk in the app runs forward from the bucket's anchor and returns
/// the stored figure for anything earlier — and recording a payment re-anchors to the payment date. So after any
/// payment, every past date answered with today's balance and the "Debt owed" trend was a dead-straight line
/// reading "No change over this window" on an account that had just paid a loan down.
/// </summary>
public class DebtHistoryTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    private static SavingCategory Loan(decimal balance = 20_000m, decimal rate = 6m, decimal installment = 400m,
        DateOnly? asOf = null)
    {
        var s = new SavingCategory("Car loan");
        s.ConfigureDebt(balance, rate, installment, originalBalance: balance, balanceAsOf: asOf ?? Jan);
        return s;
    }

    [Fact]
    public void Walking_back_undoes_walking_forward()
    {
        // The exact algebraic inverse: forward is b(1+r) − P, back is (b + P) / (1 + r).
        var after = LoanForecast.BalanceAfter(20_000m, 6m, 400m, 6);
        Assert.Equal(20_000m, LoanForecast.BalanceBefore(after, 6m, 400m, 6), precision: 2);
    }

    [Fact]
    public void A_zero_rate_loan_reverses_by_the_plain_installment()
    {
        Assert.Equal(21_200m, LoanForecast.BalanceBefore(20_000m, 0m, 400m, 3));
    }

    [Fact]
    public void Before_the_anchor_the_balance_was_higher_not_the_same()
    {
        // The bug in one assertion: DebtBalanceOn answers "today's figure" for a past date, which is what drew the
        // flat line. DebtOwedOn walks the schedule back instead.
        var loan = Loan(asOf: Jan);
        var sixMonthsEarlier = Jan.AddMonths(-6);

        Assert.Equal(20_000m, loan.DebtBalanceOn(sixMonthsEarlier));          // the old, flat answer
        Assert.True(loan.DebtOwedOn(sixMonthsEarlier, extraRepaidSince: 0m) > 20_000m);
    }

    [Fact]
    public void A_prepayment_made_since_is_added_back()
    {
        // Standing on a date before a €5,000 prepayment, the loan had not yet received it — so the reconstructed
        // balance must be about €5,000 higher than the same date reconstructed without it.
        var loan = Loan(asOf: Jan);
        var earlier = Jan.AddMonths(-3);

        var withoutPrepayment = loan.DebtOwedOn(earlier, extraRepaidSince: 0m);
        var withPrepayment = loan.DebtOwedOn(earlier, extraRepaidSince: 5_000m);

        Assert.True(withPrepayment > withoutPrepayment);
        // Reversed over three months the restored principal shrinks slightly (it was accruing interest), so this is
        // "about 5,000", not exactly — but it must be most of it, never a rounding-sized difference.
        Assert.InRange(withPrepayment - withoutPrepayment, 4_800m, 5_000m);
    }

    [Fact]
    public void On_or_after_the_anchor_nothing_changes()
    {
        // Forward of the anchor the existing walk is already right, and this must not disturb it.
        var loan = Loan(asOf: Jan);
        var later = Jan.AddMonths(4);
        Assert.Equal(loan.DebtBalanceOn(later), loan.DebtOwedOn(later, extraRepaidSince: 0m));
    }

    [Fact]
    public void The_account_totals_its_debts_and_restores_dated_prepayments()
    {
        // End to end: a loan, a bucket funded then deployed at it, and the question "what did I owe in January?"
        var account = new Account("Household", "EUR");
        var period = account.StartPeriod(Jan, new DateOnly(2026, 1, 31));
        var fund = account.AddFund("Cash");
        period.SetInitialBalance(fund.Id, new Money(30_000m, "EUR"));

        var loan = account.AddSavingCategory("Car loan");
        loan.ConfigureDebt(20_000m, 6m, 400m, originalBalance: 20_000m, balanceAsOf: Jan);

        // Deploy €5,000 at the loan in March, which lowers the balance and re-anchors to March.
        var march = new DateOnly(2026, 3, 15);
        period.DisburseSaving(loan.Id, fund.Id, new Money(5_000m, "EUR"), march);
        account.RecordSavingDebtPayment(loan.Id, 5_000m, march);

        Assert.Equal(5_000m, account.ExtraRepaidAfter(loan.Id, Jan));
        Assert.Equal(0m, account.ExtraRepaidAfter(loan.Id, march));

        // January is before the March prepayment, so the reconstruction must put that €5,000 back on the loan.
        var owedInJanuary = account.DebtOwedOn(Jan);
        Assert.True(owedInJanuary > 19_000m,
            $"January should still owe roughly the original €20,000, not the post-prepayment figure; got {owedInJanuary}");
    }
}
