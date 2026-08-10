using FinApp.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// What a one-off overpayment buys. The point of reporting BOTH outcomes is that the same money can only buy one:
/// finishing sooner (worth more interest) or a smaller monthly payment (worth more breathing room). Reporting only
/// the interest saved would quietly recommend the first.
/// </summary>
public class LoanLumpSumTests
{
    [Fact]
    public void An_overpayment_shortens_the_term_and_saves_interest()
    {
        var ls = LoanForecast.PayLumpSum(10_000m, 6m, 300m, 2_000m);

        Assert.NotNull(ls);
        Assert.True(ls!.Value.AfterKeepingInstallment.Months < ls.Value.Before.Months);
        Assert.True(ls.Value.MonthsSaved > 0);
        Assert.True(ls.Value.InterestSaved > 0m);
        Assert.False(ls.Value.ClearsTheLoan);
    }

    [Fact]
    public void The_interest_saved_is_the_difference_between_the_two_schedules()
    {
        var before = LoanForecast.PayOff(10_000m, 6m, 300m)!.Value;
        var after = LoanForecast.PayOff(8_000m, 6m, 300m)!.Value;
        var ls = LoanForecast.PayLumpSum(10_000m, 6m, 300m, 2_000m)!.Value;

        Assert.Equal(decimal.Round(before.TotalInterest - after.TotalInterest, 2), ls.InterestSaved);
        Assert.Equal(before.Months - after.Months, ls.MonthsSaved);
    }

    [Fact]
    public void The_lowered_installment_clears_the_remainder_over_the_original_term()
    {
        // The other thing a lender does with the same money: keep the end date, drop the payment.
        var ls = LoanForecast.PayLumpSum(10_000m, 6m, 300m, 2_000m)!.Value;

        var lowered = ls.LoweredInstallment;
        Assert.NotNull(lowered);
        Assert.True(lowered < 300m);   // it is genuinely lower than what they pay now

        // Paying the lowered amount really does finish on the original schedule (±1 month for rounding).
        var recast = LoanForecast.PayOff(8_000m, 6m, lowered!.Value)!.Value;
        Assert.InRange(recast.Months, ls.Before.Months - 1, ls.Before.Months + 1);
    }

    [Fact]
    public void Paying_more_than_is_owed_clears_the_loan()
    {
        var ls = LoanForecast.PayLumpSum(1_000m, 6m, 300m, 5_000m);

        Assert.NotNull(ls);
        Assert.True(ls!.Value.ClearsTheLoan);
        Assert.Equal(0, ls.Value.AfterKeepingInstallment.Months);
        Assert.Equal(0m, ls.Value.LoweredInstallment);   // no payment left to lower
    }

    [Fact]
    public void A_loan_with_no_workable_schedule_reports_nothing_rather_than_guessing()
    {
        // An installment that can't out-run the interest never clears, so there is no "before" to compare against
        // and no honest claim to make about what a lump sum saves.
        Assert.Null(LoanForecast.PayLumpSum(10_000m, 24m, 50m, 1_000m));
        Assert.Null(LoanForecast.PayLumpSum(10_000m, 6m, 300m, 0m));
    }

    [Fact]
    public void A_zero_rate_loan_saves_no_interest_but_still_finishes_sooner()
    {
        var ls = LoanForecast.PayLumpSum(6_000m, 0m, 500m, 1_000m)!.Value;

        Assert.Equal(0m, ls.InterestSaved);      // there was never any interest to save
        Assert.Equal(2, ls.MonthsSaved);         // 12 months → 10
    }
}
