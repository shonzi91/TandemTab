using FinApp.Domain.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

public class LoanForecastTests
{
    [Fact]
    public void Zero_interest_loan_is_balance_over_payment()
    {
        var p = LoanForecast.PayOff(balance: 1000m, annualRatePercent: 0m, monthlyPayment: 100m);
        Assert.NotNull(p);
        Assert.Equal(10, p!.Value.Months);
        Assert.Equal(0m, p.Value.TotalInterest);
    }

    [Fact]
    public void Interest_bearing_loan_takes_longer_and_costs_interest()
    {
        // €10,000 at 12% APR (1%/month), €300/month.
        var p = LoanForecast.PayOff(10_000m, 12m, 300m);
        Assert.NotNull(p);
        Assert.InRange(p!.Value.Months, 40, 42);          // ~41 months
        Assert.InRange(p.Value.TotalInterest, 2100m, 2350m);   // ~€2,225 interest
    }

    [Fact]
    public void A_payment_that_cant_cover_interest_never_clears()
    {
        // €10,000 at 12% APR needs > €100/mo just to cover interest.
        Assert.Null(LoanForecast.PayOff(10_000m, 12m, 100m));
        Assert.Null(LoanForecast.PayOff(10_000m, 12m, 80m));
    }

    [Fact]
    public void Already_paid_off_is_zero_months()
    {
        Assert.Equal(new LoanForecast.Payoff(0, 0m), LoanForecast.PayOff(0m, 10m, 100m));
    }

    [Fact]
    public void Paying_extra_clears_it_sooner_and_saves_interest()
    {
        var sim = LoanForecast.SimulateExtra(10_000m, 12m, 300m, extraPerMonth: 200m);
        Assert.NotNull(sim);
        Assert.True(sim!.Value.WithExtra.Months < sim.Value.Base.Months);
        Assert.True(sim.Value.MonthsSaved > 0);
        Assert.True(sim.Value.InterestSaved > 0m);
    }
}
