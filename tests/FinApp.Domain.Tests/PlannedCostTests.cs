using FinApp.Domain.Accounts;
using FinApp.Domain.Savings;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>The "expenses fund" sinking-fund math: a bucket lists its irregular future costs (insurance, tax, a
/// one-off residual) and the app averages them into a flat monthly set-aside so the money's there when a bill lands.</summary>
public class PlannedCostTests
{
    private static readonly DateOnly AsOf = new(2026, 1, 1);

    [Theory]
    [InlineData(CostCadence.Monthly, 100, 100)]     // already monthly
    [InlineData(CostCadence.Quarterly, 300, 100)]   // every 3 months → /3
    [InlineData(CostCadence.Yearly, 1200, 100)]     // once a year → /12
    public void Recurring_cost_annualises_to_a_monthly_share(CostCadence cadence, decimal amount, decimal expected)
    {
        var cost = new PlannedCost("x", amount, cadence);
        Assert.Equal(expected, cost.MonthlyAmount(AsOf));
    }

    [Fact]
    public void Dated_one_off_spreads_across_the_months_until_due()
    {
        // €3,000 needed in 6 months → €500/mo.
        var residual = new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2026, 7, 1));
        Assert.Equal(500m, residual.MonthlyAmount(AsOf));
    }

    [Fact]
    public void Due_now_or_overdue_one_off_asks_for_the_whole_amount_this_month()
    {
        var dueNow = new PlannedCost("Now", 900m, CostCadence.OneOff, AsOf);
        var overdue = new PlannedCost("Late", 900m, CostCadence.OneOff, new DateOnly(2025, 12, 1));
        Assert.Equal(900m, dueNow.MonthlyAmount(AsOf));
        Assert.Equal(900m, overdue.MonthlyAmount(AsOf));   // never divides by a non-positive month count
    }

    [Fact]
    public void Undated_one_off_contributes_nothing_to_the_running_average()
    {
        var target = new PlannedCost("Someday", 5_000m, CostCadence.OneOff);
        Assert.Equal(0m, target.MonthlyAmount(AsOf));
    }

    [Fact]
    public void Bucket_sums_its_costs_into_one_monthly_set_aside()
    {
        // The user's car: insurance €400/yr, road tax €180/yr, maintenance €300/yr, €3,000 residual by Jun 2027.
        var account = new Account("Home", "EUR");
        var car = account.AddSavingCategory("Car");
        account.SetSavingCosts(car.Id, new[]
        {
            new PlannedCost("Insurance", 400m, CostCadence.Yearly),
            new PlannedCost("Road tax", 180m, CostCadence.Yearly),
            new PlannedCost("Maintenance", 300m, CostCadence.Yearly),
            new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 6, 1)),
        });

        // (400 + 180 + 300)/12 = 73.33; residual 3000 / 17 months = 176.47 → 249.80.
        var bucket = account.FindSavingCategory(car.Id)!;
        Assert.True(bucket.HasCosts);
        Assert.Equal(249.80m, bucket.MonthlySetAside(AsOf));
    }

    [Fact]
    public void Blank_or_zero_cost_lines_are_dropped_on_replace()
    {
        var account = new Account("Home", "EUR");
        var car = account.AddSavingCategory("Car");
        account.SetSavingCosts(car.Id, new[]
        {
            new PlannedCost("Insurance", 400m, CostCadence.Yearly),
            new PlannedCost("  ", 100m, CostCadence.Yearly),   // blank label → dropped
            new PlannedCost("Free", 0m, CostCadence.Yearly),   // zero amount → dropped
        });

        var bucket = account.FindSavingCategory(car.Id)!;
        Assert.Single(bucket.Costs);
        Assert.Equal("Insurance", bucket.Costs[0].Label);
    }
}
