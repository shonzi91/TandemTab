using FinApp.Domain.Forecasting;
using FinApp.Domain.Recurring;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>The cash-flow runway: given what already repeats, when does the money run out? Projection only — it
/// moves nothing, and deliberately ignores budgets (advisory plans, not commitments).</summary>
public class CashFlowForecastTests
{
    private static readonly DateOnly From = new(2026, 7, 15);   // mid-month: rows must still be dated to the 1st

    private static RecurringItem Bill(decimal amount, RecurringAmountMode mode = RecurringAmountMode.Fixed) =>
        new("Bill", RecurringKind.Expense, mode, amount, 1, Guid.NewGuid(), Guid.NewGuid());

    private static RecurringItem Income(decimal amount, RecurringAmountMode mode = RecurringAmountMode.Fixed) =>
        new("Salary", RecurringKind.Income, mode, amount, 25, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void A_surplus_compounds_forward_month_on_month()
    {
        var p = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), Bill(1_500m) }, 0m, From, 3);

        Assert.Equal(3, p.Months.Count);
        Assert.Null(p.FirstShortfallMonth);
        Assert.Equal(new DateOnly(2026, 7, 1), p.Months[0].Month);   // dated to the 1st, not the 15th
        Assert.Equal(1_500m, p.Months[0].Closing);                   // 1000 + 2000 - 1500
        Assert.Equal(2_000m, p.Months[1].Closing);
        Assert.Equal(2_500m, p.Months[2].Closing);
        // Each month opens exactly where the last one closed.
        Assert.Equal(p.Months[0].Closing, p.Months[1].Opening);
        Assert.Equal(p.Months[1].Closing, p.Months[2].Opening);
    }

    [Fact]
    public void It_names_the_first_month_the_balance_goes_negative()
    {
        // 500 in hand, losing 200/month → ends month 3 at -100.
        var p = CashFlowForecast.Project(500m, new[] { Income(1_000m), Bill(1_200m) }, 0m, From, 6);

        Assert.Equal(new DateOnly(2026, 9, 1), p.FirstShortfallMonth);
        Assert.Equal(300m, p.Months[0].Closing);
        Assert.Equal(100m, p.Months[1].Closing);
        Assert.Equal(-100m, p.Months[2].Closing);
        Assert.Equal(-300m, p.Months[3].Closing);   // keeps going rather than clamping at zero
    }

    [Fact]
    public void Set_aside_is_a_real_claim_on_cash()
    {
        // The sinking fund's smoothed monthly figure leaves the spendable balance, exactly like a bill.
        var withoutFund = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), Bill(1_800m) }, 0m, From, 2);
        var withFund = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), Bill(1_800m) }, 300m, From, 2);

        Assert.Equal(1_200m, withoutFund.Months[0].Closing);   // +200/mo
        Assert.Equal(900m, withFund.Months[0].Closing);        // -100/mo once 300 is committed
        Assert.Equal(300m, withFund.Months[0].SetAside);

        // The direction of travel flips: a surplus becomes a slow drain, and with enough committed it runs dry.
        Assert.Null(withoutFund.FirstShortfallMonth);
        var heavy = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), Bill(1_800m) }, 600m, From, 6);
        Assert.Equal(new DateOnly(2026, 9, 1), heavy.FirstShortfallMonth);   // -400/mo → 1,000 gone in month 3
    }

    [Fact]
    public void Amount_less_reminders_are_skipped_but_the_projection_admits_it()
    {
        // ReminderOnly claims no amount, so it can't be projected — but staying silent would present an
        // optimistic figure as a complete one.
        var p = CashFlowForecast.Project(
            1_000m, new[] { Income(2_000m), Bill(500m), Bill(0m, RecurringAmountMode.ReminderOnly) }, 0m, From, 2);

        Assert.True(p.HasUnknownAmounts);
        Assert.Equal(500m, p.Months[0].Bills);          // only the known bill counted
        Assert.Equal(2_500m, p.Months[0].Closing);

        var known = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), Bill(500m) }, 0m, From, 2);
        Assert.False(known.HasUnknownAmounts);
    }

    [Fact]
    public void Inactive_items_are_left_out_entirely()
    {
        var paused = Bill(900m);
        paused.SetActive(false);

        var p = CashFlowForecast.Project(1_000m, new[] { Income(2_000m), paused }, 0m, From, 1);

        Assert.Equal(0m, p.Months[0].Bills);
        Assert.Equal(3_000m, p.Months[0].Closing);
        Assert.False(p.HasUnknownAmounts);   // an inactive reminder-only item shouldn't raise the flag either
    }

    [Fact]
    public void An_account_with_nothing_recurring_projects_a_flat_line_rather_than_failing()
    {
        var p = CashFlowForecast.Project(750m, Array.Empty<RecurringItem>(), 0m, From, 3);

        Assert.Equal(3, p.Months.Count);
        Assert.All(p.Months, m => Assert.Equal(750m, m.Closing));
        Assert.Null(p.FirstShortfallMonth);
        Assert.False(p.HasUnknownAmounts);
    }

    [Fact]
    public void A_balance_that_starts_negative_reports_the_very_first_month()
    {
        var p = CashFlowForecast.Project(-50m, new[] { Income(1_000m), Bill(1_000m) }, 0m, From, 2);
        Assert.Equal(new DateOnly(2026, 7, 1), p.FirstShortfallMonth);
    }

    [Theory]
    [InlineData(0, 1)]      // clamped up — a zero-month projection is meaningless
    [InlineData(-5, 1)]
    [InlineData(999, CashFlowForecast.MaxMonths)]
    public void The_horizon_is_clamped_to_something_defensible(int asked, int expected)
    {
        var p = CashFlowForecast.Project(100m, Array.Empty<RecurringItem>(), 0m, From, asked);
        Assert.Equal(expected, p.Months.Count);
    }

    [Fact]
    public void A_negative_set_aside_cannot_conjure_money()
    {
        var p = CashFlowForecast.Project(100m, Array.Empty<RecurringItem>(), -500m, From, 1);
        Assert.Equal(0m, p.Months[0].SetAside);
        Assert.Equal(100m, p.Months[0].Closing);
    }

    [Fact]
    public void Projection_rows_run_in_order_across_a_year_boundary()
    {
        var p = CashFlowForecast.Project(0m, Array.Empty<RecurringItem>(), 0m, new DateOnly(2026, 11, 10), 4);

        Assert.Equal(new DateOnly(2026, 11, 1), p.Months[0].Month);
        Assert.Equal(new DateOnly(2026, 12, 1), p.Months[1].Month);
        Assert.Equal(new DateOnly(2027, 1, 1), p.Months[2].Month);
        Assert.Equal(new DateOnly(2027, 2, 1), p.Months[3].Month);
    }
}
