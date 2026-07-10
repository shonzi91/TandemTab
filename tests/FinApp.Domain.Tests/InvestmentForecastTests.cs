using FinApp.Domain.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

public class InvestmentForecastTests
{
    [Fact]
    public void Zero_rate_is_present_plus_contributions()
    {
        var p = InvestmentForecast.Project(present: 500m, annualRatePercent: 0m, termYears: 1m, compoundsPerYear: 12, monthlyContribution: 100m);
        Assert.Equal(12, p.Months);
        Assert.Equal(1700m, p.FutureValue);   // 500 + 12×100, no growth
        Assert.Equal(1200m, p.Contributed);
        Assert.Equal(0m, p.Growth);
    }

    [Fact]
    public void Compound_growth_on_principal_matches_annual_rate()
    {
        // €1,000 at 12% nominal compounded monthly for 1 year, no contributions ≈ 1000 × 1.01^12.
        var p = InvestmentForecast.Project(present: 1000m, annualRatePercent: 12m, termYears: 1m, compoundsPerYear: 12, monthlyContribution: 0m);
        Assert.Equal(12, p.Months);
        Assert.Equal(0m, p.Contributed);
        Assert.InRange(p.FutureValue, 1126m, 1127m);   // 1126.83
        Assert.InRange(p.Growth, 126m, 127m);
    }

    [Fact]
    public void Contributions_are_grown_too_so_extra_beats_leaving_it()
    {
        var alone = InvestmentForecast.Project(1000m, 6m, 10m, 12, 0m);
        var adding = InvestmentForecast.Project(1000m, 6m, 10m, 12, 200m);
        Assert.True(adding.FutureValue > alone.FutureValue);
        Assert.True(adding.Contributed == 24000m);                 // 120 months × 200
        Assert.True(adding.Growth > adding.FutureValue - 1000m - adding.Contributed - 1m);  // some growth on the contributions
    }

    [Fact]
    public void Zero_term_returns_present_value_untouched()
    {
        var p = InvestmentForecast.Project(1234.56m, 8m, 0m, 12, 100m);
        Assert.Equal(0, p.Months);
        Assert.Equal(1234.56m, p.FutureValue);
        Assert.Equal(0m, p.Contributed);
    }
}
