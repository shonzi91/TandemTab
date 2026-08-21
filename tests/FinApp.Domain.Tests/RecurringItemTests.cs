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
    [InlineData(31, 31)]
    [InlineData(40, 31)]
    [InlineData(15, 15)]
    public void DayOfMonth_is_clamped_to_a_real_calendar_day(int given, int expected)
    {
        // 1–31, not the old 1–28: a loan can contractually fall due on the 30th, and a bill servicing it has to be
        // able to state the same day. Short months are handled where it matters — see DueDateWithin below.
        var item = Make(day: given);
        Assert.Equal(expected, item.DayOfMonth);
    }

    [Fact]
    public void A_day_past_the_end_of_a_short_month_falls_due_on_its_last_day()
    {
        // This is what makes storing 31 safe: the day is pulled back to the month's end rather than overflowing
        // into the next one or throwing.
        var item = Make(day: 31);
        Assert.Equal(new DateOnly(2026, 2, 28), item.DueDateWithin(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        Assert.Equal(new DateOnly(2026, 3, 31), item.DueDateWithin(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
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
        Assert.Equal(31, item.DayOfMonth);       // clamped to the longest real month
        Assert.Equal(cat, item.CategoryId);
        Assert.Equal(fund, item.FundId);
        Assert.Equal("💡", item.Icon);
        Assert.False(item.AutoPost);              // forced off — no longer Fixed
    }

    // ── An item never falls due for a date that precedes it ──────────────────────────────────
    private static readonly DateOnly PFrom = new(2026, 7, 1);
    private static readonly DateOnly PTo = new(2026, 7, 31);

    [Fact]
    public void An_item_added_after_its_day_has_passed_does_not_fire_this_period()
    {
        // Set up "rent, day 10" on the 19th. That describes an arrangement going forward — not a payment you
        // forgot to log on the 10th. With AutoPost on, treating it as due would silently post a dated expense.
        var item = Make(day: 10, autoPost: true);
        item.SetCreatedOn(new DateOnly(2026, 7, 19));

        Assert.False(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 19)));
        Assert.False(item.IsPending(PFrom, PTo));
        Assert.False(item.IsUpcoming(PFrom, PTo, new DateOnly(2026, 7, 19), 30));
    }

    [Fact]
    public void It_starts_firing_from_the_next_period()
    {
        var item = Make(day: 10, autoPost: true);
        item.SetCreatedOn(new DateOnly(2026, 7, 19));

        var augFrom = new DateOnly(2026, 8, 1);
        var augTo = new DateOnly(2026, 8, 31);
        Assert.True(item.IsDue(augFrom, augTo, new DateOnly(2026, 8, 10)));
        Assert.True(item.IsPending(augFrom, augTo));
    }

    [Fact]
    public void An_item_added_before_its_day_still_fires_this_period()
    {
        // Added on the 5th for day 25 — the day is genuinely still ahead, so this period counts.
        var item = Make(day: 25);
        item.SetCreatedOn(new DateOnly(2026, 7, 5));

        Assert.True(item.IsPending(PFrom, PTo));
        Assert.True(item.IsUpcoming(PFrom, PTo, new DateOnly(2026, 7, 20), 10));
        Assert.False(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 20)));   // not yet — day hasn't arrived
        Assert.True(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 25)));
    }

    [Fact]
    public void An_item_added_on_its_own_due_day_counts_that_day()
    {
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 10));
        Assert.True(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 10)));
    }

    [Fact]
    public void Items_from_before_this_was_tracked_keep_their_old_behaviour()
    {
        // No creation date to compare against, so nothing is suppressed — inventing one would be a guess that
        // could silence a bill that should genuinely fire.
        var item = Make(day: 10);
        Assert.Null(item.CreatedOn);
        Assert.True(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 19)));
        Assert.True(item.IsPending(PFrom, PTo));
    }

    // ── StartsLater: the third population of the "already this period" section ───────────────────
    // Both list clients split on !IsPending, so a not-yet-started item lands under a heading claiming it has
    // happened. These pin the one fact that separates it from a bill that really was paid.

    [Fact]
    public void An_item_that_has_not_started_yet_says_so()
    {
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 19));

        Assert.False(item.IsPending(PFrom, PTo));      // why it sits in the lower section at all
        Assert.True(item.StartsLater(PFrom, PTo));     // and why it must not read as handled there
    }

    [Fact]
    public void A_bill_actually_handled_this_period_is_not_starting_later()
    {
        // The case the marker exists to be distinguishable from: same section, same !IsPending, opposite meaning.
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 1));
        item.MarkHandled(PFrom);

        Assert.False(item.IsPending(PFrom, PTo));
        Assert.False(item.StartsLater(PFrom, PTo));
    }

    [Fact]
    public void A_skipped_bill_is_not_starting_later()
    {
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 1));
        item.MarkHandled(PFrom, skipped: true);

        Assert.True(item.SkippedIn(PFrom));
        Assert.False(item.StartsLater(PFrom, PTo));
    }

    [Fact]
    public void A_paused_item_is_not_starting_later_even_when_it_has_not_started()
    {
        // Both would be true of it, and the row can only say one thing. "Paused" is the more actionable of the
        // two — it names something the user did and can undo — so it wins, and this keeps the marker out of its way.
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 19));
        item.SetActive(false);

        Assert.False(item.StartsLater(PFrom, PTo));
    }

    [Fact]
    public void The_marker_clears_once_the_item_has_started()
    {
        var item = Make(day: 10);
        item.SetCreatedOn(new DateOnly(2026, 7, 19));

        var augFrom = new DateOnly(2026, 8, 1);
        var augTo = new DateOnly(2026, 8, 31);
        Assert.True(item.IsPending(augFrom, augTo));
        Assert.False(item.StartsLater(augFrom, augTo));
    }

    [Fact]
    public void An_ordinary_pending_bill_is_not_starting_later()
    {
        var item = Make(day: 25);
        item.SetCreatedOn(new DateOnly(2026, 7, 5));
        Assert.False(item.StartsLater(PFrom, PTo));
    }
}
