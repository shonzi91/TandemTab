using FinApp.Domain.Recurring;
using Xunit;

namespace FinApp.Domain.Tests;

public class RecurringItemTests
{
    private static RecurringItem Make(
        RecurringKind kind = RecurringKind.Expense,
        RecurringAmountMode mode = RecurringAmountMode.Fixed,
        decimal amount = 100m,
        int day = 15,
        bool autoPost = false) =>
        new("Rent", kind, mode, amount, day, Guid.NewGuid(), Guid.NewGuid(), autoPost: autoPost);

    [Fact]
    public void Requires_a_name()
    {
        Assert.Throws<ArgumentException>(() =>
            new RecurringItem("  ", RecurringKind.Expense, RecurringAmountMode.Fixed, 10m, 1, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void ReminderOnly_carries_no_amount()
    {
        var item = Make(mode: RecurringAmountMode.ReminderOnly, amount: 500m);
        Assert.Equal(0m, item.ExpectedAmount);
        Assert.False(item.HasKnownAmount);
    }

    [Theory]
    [InlineData(RecurringAmountMode.Fixed, true)]
    [InlineData(RecurringAmountMode.Typical, false)]
    [InlineData(RecurringAmountMode.ReminderOnly, false)]
    public void AutoPost_is_only_allowed_for_fixed_amounts(RecurringAmountMode mode, bool expected)
    {
        var item = Make(mode: mode, autoPost: true);
        Assert.Equal(expected, item.AutoPost);
    }

    [Fact]
    public void Negative_amount_is_floored_to_zero()
    {
        var item = Make(amount: -50m);
        Assert.Equal(0m, item.ExpectedAmount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(31, 28)]
    [InlineData(15, 15)]
    public void DayOfMonth_is_clamped_to_a_day_that_exists_every_month(int given, int expected)
    {
        var item = Make(day: given);
        Assert.Equal(expected, item.DayOfMonth);
    }

    [Fact]
    public void DueDate_lands_within_the_period_even_in_short_months()
    {
        // Day 28 is the max, but a period may be shorter than the calendar month.
        var item = Make(day: 28);
        var from = new DateOnly(2026, 2, 1);
        var to = new DateOnly(2026, 2, 20); // period ends before the 28th
        Assert.Equal(to, item.DueDateWithin(from, to)); // clamped to the period end
    }

    [Fact]
    public void Is_due_once_its_day_arrives_and_stops_after_being_handled()
    {
        var item = Make(day: 15);
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 1, 31);

        Assert.False(item.IsDue(from, to, new DateOnly(2026, 1, 14))); // day not reached
        Assert.True(item.IsDue(from, to, new DateOnly(2026, 1, 15)));  // day reached
        Assert.True(item.IsPending(from));

        item.MarkHandled(from);
        Assert.False(item.IsDue(from, to, new DateOnly(2026, 1, 20))); // handled this period
        Assert.False(item.IsPending(from));

        // A new period re-arms it.
        Assert.True(item.IsPending(new DateOnly(2026, 2, 1)));
    }

    [Fact]
    public void Inactive_items_are_never_due_or_pending()
    {
        var item = Make(day: 1);
        item.SetActive(false);
        var from = new DateOnly(2026, 1, 1);
        Assert.False(item.IsDue(from, new DateOnly(2026, 1, 31), new DateOnly(2026, 1, 15)));
        Assert.False(item.IsPending(from));
    }

    [Fact]
    public void Upcoming_covers_pending_items_inside_the_lookahead_window_only()
    {
        var item = Make(day: 20);
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 1, 31);

        Assert.True(item.IsUpcoming(from, to, new DateOnly(2026, 1, 15), windowDays: 7));  // due in 5 days
        Assert.False(item.IsUpcoming(from, to, new DateOnly(2026, 1, 10), windowDays: 7)); // due in 10 days
        Assert.False(item.IsUpcoming(from, to, new DateOnly(2026, 1, 20), windowDays: 7)); // due today, not "upcoming"
    }

    [Fact]
    public void DaysUntilDue_is_negative_once_the_day_has_passed()
    {
        var item = Make(day: 10);
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 1, 31);
        Assert.Equal(3, item.DaysUntilDue(from, to, new DateOnly(2026, 1, 7)));
        Assert.Equal(-4, item.DaysUntilDue(from, to, new DateOnly(2026, 1, 14)));
    }

    [Fact]
    public void Typical_amount_self_tunes_halfway_toward_the_actual()
    {
        var item = Make(mode: RecurringAmountMode.Typical, amount: 100m);
        item.LearnFromActual(140m);
        Assert.Equal(120m, item.ExpectedAmount); // (100 + 140) / 2
    }

    [Theory]
    [InlineData(RecurringAmountMode.Fixed)]
    [InlineData(RecurringAmountMode.ReminderOnly)]
    public void Learning_is_a_no_op_for_non_typical_modes(RecurringAmountMode mode)
    {
        var item = Make(mode: mode, amount: 100m);
        var before = item.ExpectedAmount;
        item.LearnFromActual(140m);
        Assert.Equal(before, item.ExpectedAmount);
    }

    [Fact]
    public void Update_replaces_fields_and_re_applies_the_mode_rules()
    {
        var item = Make(mode: RecurringAmountMode.Fixed, amount: 100m, day: 15, autoPost: true);
        var cat = Guid.NewGuid();
        var fund = Guid.NewGuid();

        item.Update("Utilities", RecurringAmountMode.Typical, 80m, 40, cat, fund, "💡", autoPost: true);

        Assert.Equal("Utilities", item.Name);
        Assert.Equal(RecurringAmountMode.Typical, item.AmountMode);
        Assert.Equal(80m, item.ExpectedAmount);
        Assert.Equal(28, item.DayOfMonth);       // clamped
        Assert.Equal(cat, item.CategoryId);
        Assert.Equal(fund, item.FundId);
        Assert.Equal("💡", item.Icon);
        Assert.False(item.AutoPost);              // forced off — no longer Fixed
    }
}
