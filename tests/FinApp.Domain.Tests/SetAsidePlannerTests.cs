using FinApp.Domain.Savings;
using Xunit;

namespace FinApp.Domain.Tests;

public class SetAsidePlannerTests
{
    private static readonly DateOnly Jan = new(2026, 1, 1);

    [Fact]
    public void No_schedule_suggests_nothing()
    {
        Assert.Null(SetAsidePlanner.Suggest(SetAsideRule.None, 100m, 1000m, 0m, null, Jan));
    }

    [Fact]
    public void Installment_suggests_the_fixed_amount()
    {
        Assert.Equal(200m, SetAsidePlanner.Suggest(SetAsideRule.Installment, 200m, null, 0m, null, Jan));
    }

    [Fact]
    public void Installment_of_zero_suggests_nothing()
    {
        Assert.Null(SetAsidePlanner.Suggest(SetAsideRule.Installment, 0m, null, 0m, null, Jan));
    }

    [Fact]
    public void Split_evenly_divides_whats_left_across_the_periods_until_due()
    {
        // Goal €1,200, €200 already saved → €1,000 left; due June from a January period = 6 periods (Jan..Jun).
        var due = new DateOnly(2026, 6, 30);
        Assert.Equal(166.67m, SetAsidePlanner.Suggest(SetAsideRule.SplitEvenly, 0m, 1200m, 200m, due, Jan));
    }

    [Fact]
    public void Split_evenly_suggests_the_whole_remainder_when_due_this_period_or_past()
    {
        var thisMonth = new DateOnly(2026, 1, 20);
        Assert.Equal(800m, SetAsidePlanner.Suggest(SetAsideRule.SplitEvenly, 0m, 1000m, 200m, thisMonth, Jan));

        var past = new DateOnly(2025, 12, 1);
        Assert.Equal(800m, SetAsidePlanner.Suggest(SetAsideRule.SplitEvenly, 0m, 1000m, 200m, past, Jan));
    }

    [Fact]
    public void Split_evenly_suggests_nothing_once_the_goal_is_met_or_absent()
    {
        Assert.Null(SetAsidePlanner.Suggest(SetAsideRule.SplitEvenly, 0m, 1000m, 1000m, new DateOnly(2026, 6, 1), Jan));
        Assert.Null(SetAsidePlanner.Suggest(SetAsideRule.SplitEvenly, 0m, null, 0m, new DateOnly(2026, 6, 1), Jan));
    }

    [Theory]
    [InlineData(2026, 1, 1)]   // due in the current month → 1 period
    [InlineData(2026, 2, 2)]   // next month → 2 periods
    [InlineData(2026, 12, 12)] // December → 12 periods
    public void PeriodsRemaining_counts_inclusive_months_from_the_period_start(int y, int m, int expected)
    {
        Assert.Equal(expected, SetAsidePlanner.PeriodsRemaining(new DateOnly(y, m, 15), Jan));
    }
}
