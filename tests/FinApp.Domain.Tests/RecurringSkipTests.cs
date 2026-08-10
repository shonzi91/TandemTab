using FinApp.Domain.Accounts;
using FinApp.Domain.Recurring;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// A skip and a posting both make an item "handled", but only one of them may be undone: re-arming a posted bill
/// would leave its expense on the ledger and the bill due again — one payment, charged twice. These pin that the
/// two are told apart, and that the unsafe undo is refused rather than merely un-offered in the UI.
/// </summary>
public class RecurringSkipTests
{
    private static readonly DateOnly PFrom = new(2026, 7, 1);
    private static readonly DateOnly PTo = new(2026, 7, 31);

    private static RecurringItem Make(int day = 15) =>
        new("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 100m, day, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void A_skip_is_handled_but_recorded_as_a_skip()
    {
        var item = Make();
        item.MarkHandled(PFrom, skipped: true);

        Assert.Equal(PFrom, item.LastHandledPeriodFrom);
        Assert.True(item.LastHandledWasSkip);
        Assert.True(item.SkippedIn(PFrom));
        Assert.False(item.IsPending(PFrom, PTo));
        Assert.False(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 20)));
    }

    [Fact]
    public void A_posting_is_not_a_skip()
    {
        var item = Make();
        item.MarkHandled(PFrom);

        Assert.True(item.LastHandledPeriodFrom == PFrom);
        Assert.False(item.LastHandledWasSkip);
        Assert.False(item.SkippedIn(PFrom));
    }

    [Fact]
    public void Undoing_a_skip_makes_the_item_due_again_in_the_same_period()
    {
        var item = Make(day: 10);
        item.MarkHandled(PFrom, skipped: true);
        item.ClearHandled();

        Assert.Null(item.LastHandledPeriodFrom);
        Assert.False(item.LastHandledWasSkip);
        Assert.True(item.IsPending(PFrom, PTo));
        Assert.True(item.IsDue(PFrom, PTo, new DateOnly(2026, 7, 20)));
    }

    [Fact]
    public void A_posted_item_refuses_to_be_un_handled()
    {
        // The guard lives in the domain, not only in the UI: the expense is already on the ledger, so re-arming
        // the item is a second payment waiting to happen whatever surface asks for it.
        var item = Make();
        item.MarkHandled(PFrom);

        Assert.Throws<InvalidOperationException>(() => item.ClearHandled());
        Assert.Equal(PFrom, item.LastHandledPeriodFrom);
    }

    [Fact]
    public void A_skip_in_another_period_is_not_a_skip_in_this_one()
    {
        var item = Make();
        item.MarkHandled(new DateOnly(2026, 6, 1), skipped: true);

        Assert.False(item.SkippedIn(PFrom));
        Assert.True(item.IsPending(PFrom, PTo));
    }

    [Fact]
    public void The_skip_flag_survives_a_snapshot_round_trip()
    {
        var account = new Account("Personal", "EUR");
        var item = Make();
        item.MarkHandled(PFrom, skipped: true);
        account.AddRecurring(item);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var back = Assert.Single(restored.RecurringItems);
        Assert.True(back.SkippedIn(PFrom));
    }

    [Fact]
    public void A_snapshot_written_before_skips_were_tracked_reads_its_handled_item_as_posted()
    {
        // The conservative reading: a legacy handled item merely loses its undo, rather than being offered one
        // that could re-arm a bill whose expense is already booked.
        var account = new Account("Old", "EUR");
        var item = Make();
        item.MarkHandled(PFrom, skipped: true);
        account.AddRecurring(item);

        var legacy = AccountSnapshotSerializer.Serialize(account).Replace("\"LastHandledWasSkip\":true", "\"X\":true");
        Assert.DoesNotContain("LastHandledWasSkip", legacy);

        var back = Assert.Single(AccountSnapshotSerializer.Deserialize(legacy).RecurringItems);
        Assert.Equal(PFrom, back.LastHandledPeriodFrom);
        Assert.False(back.LastHandledWasSkip);
        Assert.Throws<InvalidOperationException>(() => back.ClearHandled());
    }
}
