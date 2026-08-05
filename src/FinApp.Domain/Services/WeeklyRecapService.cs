using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Domain.Services;

/// <summary>One week's read-out: what was spent, how that compares with the week before, and where it went.</summary>
/// <param name="From">Monday of the week covered (inclusive).</param>
/// <param name="To">Sunday of the week covered (inclusive).</param>
/// <param name="Spent">Total spent in the covered week.</param>
/// <param name="PreviousSpent">Total spent in the week before it — the comparison, not part of the headline.</param>
/// <param name="TopCategoryId">The category with the largest share, or null when nothing was spent.</param>
/// <param name="TopCategorySpent">What went to <paramref name="TopCategoryId"/>.</param>
/// <param name="Saved">Set aside during the covered week (savings allocations dated inside it).</param>
public sealed record WeeklyRecap(
    DateOnly From,
    DateOnly To,
    Money Spent,
    Money PreviousSpent,
    Guid? TopCategoryId,
    Money TopCategorySpent,
    Money Saved)
{
    /// <summary>Difference against the week before — negative means you spent less, which is the good direction.</summary>
    public Money Change => Spent - PreviousSpent;

    /// <summary>True when there is a previous week worth comparing against. Without it the change is meaningless:
    /// "100% more than last week" for someone's first week using the app is noise dressed up as insight.</summary>
    public bool HasComparison => !PreviousSpent.IsZero;

    /// <summary>Nothing happened in either week — the recap should not be shown at all rather than reporting zeros.</summary>
    public bool IsEmpty => Spent.IsZero && PreviousSpent.IsZero && Saved.IsZero;
}

/// <summary>
/// F7 — "your week in money". A pure read-out over expenses and savings already recorded; it computes nothing the
/// rest of the app doesn't already show, which is exactly why it can be a re-engagement nudge without being a claim.
///
/// <para><b>The covered week is the last COMPLETED one</b> (Monday–Sunday), not the current one. A recap of a week
/// still in progress changes every time you open it and its "vs last week" compares three days against seven —
/// which reads as a spending collapse every Tuesday. A finished week is a fact.</para>
///
/// <para>Weeks are walked across the whole account rather than within a period: a calendar week routinely straddles
/// two monthly periods, and a recap that silently dropped the days either side of the 1st would be wrong for
/// roughly a quarter of the weeks in a year.</para>
/// </summary>
public sealed class WeeklyRecapService
{
    /// <summary>The Monday on or before <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));   // Sunday(0) → back 6, Monday(1) → back 0

    /// <summary>The Monday of the most recently completed week, relative to <paramref name="today"/>.</summary>
    public static DateOnly LastCompletedWeekStart(DateOnly today) => WeekStart(today).AddDays(-7);

    /// <summary>Build the recap for the last completed week, or null when the account has no periods at all.</summary>
    public WeeklyRecap? Build(Account account, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Periods.Count == 0) return null;

        var from = LastCompletedWeekStart(today);
        var to = from.AddDays(6);
        var prevFrom = from.AddDays(-7);

        var expenses = account.Periods.SelectMany(p => p.Expenses).ToList();
        var zero = Money.Zero(account.Currency);

        Money SpentBetween(DateOnly a, DateOnly b) =>
            expenses.Where(e => e.Date >= a && e.Date <= b)
                .Aggregate(zero, (acc, e) => acc + e.Amount);

        var spent = SpentBetween(from, to);
        var previous = SpentBetween(prevFrom, from.AddDays(-1));

        // Materialized rather than FirstOrDefault'd: default(Money) carries no currency, and adding it to anything
        // would either throw or silently produce a currency-less total.
        var byCategory = expenses.Where(e => e.Date >= from && e.Date <= to)
            .GroupBy(e => e.CategoryId)
            .Select(g => (CategoryId: g.Key, Total: g.Aggregate(zero, (acc, e) => acc + e.Amount)))
            .OrderByDescending(x => x.Total.Amount)
            .ToList();
        Guid? topCategoryId = byCategory.Count > 0 ? byCategory[0].CategoryId : null;
        var topSpent = byCategory.Count > 0 ? byCategory[0].Total : zero;

        // Disbursements (savings deployed to their goal) are excluded: they are a bucket being spent for its
        // purpose, not money set aside this week, and counting them would report a negative week of "saving".
        var saved = account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Where(a => a.Date >= from && a.Date <= to && !a.IsDisbursement && !a.Amount.IsNegative)
            .Aggregate(zero, (acc, a) => acc + a.Amount);

        return new WeeklyRecap(from, to, spent, previous, topCategoryId, topSpent, saved);
    }
}
