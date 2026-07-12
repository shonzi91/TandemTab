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
    public void MonthlyInterest_is_balance_times_apr_over_twelve()
    {
        // €10,000 at 12% APR = 1%/month = €100 this month.
        Assert.Equal(100m, LoanForecast.MonthlyInterest(10_000m, 12m));
        // A €2,000 partial payment drops the balance to €8,000 → €80/month, i.e. €20 less every month.
        Assert.Equal(80m, LoanForecast.MonthlyInterest(8_000m, 12m));
    }

    [Fact]
    public void MonthlyInterest_is_zero_without_a_balance_or_rate()
    {
        Assert.Equal(0m, LoanForecast.MonthlyInterest(0m, 12m));
        Assert.Equal(0m, LoanForecast.MonthlyInterest(-500m, 12m));
        Assert.Equal(0m, LoanForecast.MonthlyInterest(10_000m, 0m));
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

    private static readonly LoanForecast.LoanInput[] TwoLoans =
    {
        // Small balance but the cheaper rate.
        new(Guid.NewGuid(), "Card A", 2_000m, 8m, 60m),
        // Bigger balance but the pricier rate.
        new(Guid.NewGuid(), "Card B", 6_000m, 22m, 150m),
    };

    [Fact]
    public void Avalanche_targets_the_priciest_debt_first()
    {
        var plan = LoanForecast.PlanPayoff(TwoLoans, extraPerMonth: 300m, LoanForecast.Strategy.Avalanche);
        Assert.NotNull(plan);
        // Card B carries the higher rate, so avalanche should clear it before the cheaper Card A.
        Assert.Equal("Card B", plan!.Value.Order[0].Name);
        Assert.Equal(2, plan.Value.Order.Count);
        Assert.True(plan.Value.Months > 0);
    }

    [Fact]
    public void Snowball_targets_the_smallest_balance_first()
    {
        var plan = LoanForecast.PlanPayoff(TwoLoans, extraPerMonth: 300m, LoanForecast.Strategy.Snowball);
        Assert.NotNull(plan);
        // Card A has the smaller balance, so snowball should knock it out first.
        Assert.Equal("Card A", plan!.Value.Order[0].Name);
    }

    [Fact]
    public void Avalanche_never_costs_more_interest_than_snowball()
    {
        var avalanche = LoanForecast.PlanPayoff(TwoLoans, 300m, LoanForecast.Strategy.Avalanche);
        var snowball = LoanForecast.PlanPayoff(TwoLoans, 300m, LoanForecast.Strategy.Snowball);
        Assert.NotNull(avalanche);
        Assert.NotNull(snowball);
        Assert.True(avalanche!.Value.TotalInterest <= snowball!.Value.TotalInterest);
    }

    [Fact]
    public void A_stack_that_cant_out_run_interest_never_clears()
    {
        var stuck = new LoanForecast.LoanInput[] { new(Guid.NewGuid(), "Trap", 10_000m, 24m, 100m) };
        Assert.Null(LoanForecast.PlanPayoff(stuck, extraPerMonth: 0m, LoanForecast.Strategy.Avalanche));
    }

    [Fact]
    public void Nothing_to_pay_returns_null()
    {
        Assert.Null(LoanForecast.PlanPayoff(Array.Empty<LoanForecast.LoanInput>(), 100m, LoanForecast.Strategy.Snowball));
    }
}
