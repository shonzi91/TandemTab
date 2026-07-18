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
    public void A_part_funded_target_only_asks_for_what_is_still_missing()
    {
        // The bug this fixes: €3,000 residual due in 44 months with €1,000 already put away was asking
        // 3000/44 = €68.18/mo — charging for money that's already in the bucket. It needs 2000/44 = €45.45.
        var residual = new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2029, 9, 1));

        Assert.Equal(3_000m / 44m, residual.MonthlyAmount(AsOf));               // unfunded, unchanged
        Assert.Equal(2_000m / 44m, residual.MonthlyAmount(AsOf, 1_000m));       // funded, discounted
    }

    [Fact]
    public void A_fully_funded_target_asks_for_nothing_and_never_goes_negative()
    {
        var residual = new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 1, 1));
        Assert.Equal(0m, residual.MonthlyAmount(AsOf, 3_000m));
        Assert.Equal(0m, residual.MonthlyAmount(AsOf, 4_500m));   // over-funded, still zero — not a refund
        Assert.Equal(0m, residual.Remaining(4_500m));
    }

    [Theory]
    [InlineData(CostCadence.Monthly, 100, 100)]
    [InlineData(CostCadence.Quarterly, 500, 500 / 3d)]
    [InlineData(CostCadence.Yearly, 1200, 100)]
    public void A_recurring_cost_is_a_rate_so_savings_do_not_discount_it(CostCadence cadence, decimal amount, double expected)
    {
        // The user's insurance: €500 every 3 months → €166.67/mo, forever. Holding €500 today doesn't mean
        // this month is free — next year's insurance is behind this one. Discounting would make the ask
        // collapse to zero whenever the bucket is full and spike the month after the bill lands.
        var cost = new PlannedCost("Insurance", amount, cadence);
        Assert.Equal((decimal)expected, cost.MonthlyAmount(AsOf, 10_000m), 6);
    }

    [Fact]
    public void Savings_cover_the_soonest_target_first()
    {
        var account = new Account("Home", "EUR");
        var car = account.AddSavingCategory("Car");
        account.SetSavingCosts(car.Id, new[]
        {
            new PlannedCost("Deposit", 1_000m, CostCadence.OneOff, new DateOnly(2026, 3, 1)),   // 2 months out
            new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 1, 1)), // 12 months out
        });
        var bucket = account.FindSavingCategory(car.Id)!;

        // €1,000 saved clears the near deposit entirely; nothing spills onto the residual beyond that.
        // Deposit: (1000-1000)/2 = 0.  Residual: 3000/12 = 250.
        Assert.Equal(250m, bucket.MonthlySetAside(AsOf, 1_000m));

        // Unfunded, both are asked for: 1000/2 + 3000/12 = 500 + 250 = 750.
        Assert.Equal(750m, bucket.MonthlySetAside(AsOf, 0m));
    }

    [Fact]
    public void The_shortfall_read_is_what_targets_still_need_today()
    {
        var account = new Account("Home", "EUR");
        var car = account.AddSavingCategory("Car");
        account.SetSavingCosts(car.Id, new[]
        {
            new PlannedCost("Insurance", 500m, CostCadence.Quarterly),                          // a rate, not a target
            new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 1, 1)),
        });
        var bucket = account.FindSavingCategory(car.Id)!;

        Assert.Equal(3_000m, bucket.TargetShortfall(0m));
        Assert.Equal(1_800m, bucket.TargetShortfall(1_200m));
        Assert.Equal(0m, bucket.TargetShortfall(3_000m));
        Assert.Equal(0m, bucket.TargetShortfall(9_999m));   // never negative
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
