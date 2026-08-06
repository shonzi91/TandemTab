using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;

namespace FinApp.Domain.Services;

/// <summary>The single largest expense of the week — the line most people can actually place in memory.</summary>
/// <param name="Amount">What it cost.</param>
/// <param name="CategoryId">The category it was filed under.</param>
/// <param name="Date">The day it happened.</param>
/// <param name="Note">Its note, when it has one — usually the only thing that names the purchase.</param>
public sealed record WeeklyRecapExpense(Money Amount, Guid CategoryId, DateOnly Date, string? Note);

/// <summary>One label's share of the week — used for both the category and the tag breakdowns.</summary>
public sealed record WeeklyRecapSlice(Guid Id, Money Total, int Count);

/// <summary>One week's read-out: what was spent, how that compares with the week before, and where it went.</summary>
/// <param name="From">Monday of the week covered (inclusive).</param>
/// <param name="To">Sunday of the week covered (inclusive).</param>
/// <param name="Spent">Total spent in the covered week.</param>
/// <param name="PreviousSpent">Total spent in the week before it — the comparison, not part of the headline.</param>
/// <param name="TopCategoryId">The category with the largest share, or null when nothing was spent.</param>
/// <param name="TopCategorySpent">What went to <paramref name="TopCategoryId"/>.</param>
/// <param name="Saved">Set aside during the covered week (savings allocations dated inside it).</param>
/// <param name="ExpenseCount">How many separate expenses were logged — the "how often did I reach for my card"
/// figure, which a total alone hides: one €200 week and forty €5 days are different habits.</param>
/// <param name="Income">Money that came in during the week (deposits dated inside it).</param>
/// <param name="Biggest">The single largest expense, or null when nothing was spent.</param>
/// <param name="Categories">Every category touched, largest first.</param>
/// <param name="Tags">Every tag used, largest first. Empty for accounts that don't tag — which is most of them,
/// so the modal must not treat it as a missing section.</param>
public sealed record WeeklyRecap(
    DateOnly From,
    DateOnly To,
    Money Spent,
    Money PreviousSpent,
    Guid? TopCategoryId,
    Money TopCategorySpent,
    Money Saved,
    // Appended with defaults rather than inserted: this record is built in one place but read in several, and a
    // positional record punishes reordering. The six above are the card; these six are what the modal adds.
    int ExpenseCount = 0,
    Money Income = default,
    WeeklyRecapExpense? Biggest = null,
    IReadOnlyList<WeeklyRecapSlice>? Categories = null,
    IReadOnlyList<WeeklyRecapSlice>? Tags = null,
    // A steady "what this account earns in a week" — see WeeklyRecapService.Build. Zero when there's no basis for it
    // (no income ever received and no recurring income set up), in which case "left over" falls back to literal
    // in-week cash flow. Present so a week with no salary deposit doesn't read as a pure loss.
    Money TypicalWeeklyIncome = default)
{
    /// <summary>Difference against the week before — negative means you spent less, which is the good direction.</summary>
    public Money Change => Spent - PreviousSpent;

    /// <summary>True when there is a previous week worth comparing against. Without it the change is meaningless:
    /// "100% more than last week" for someone's first week using the app is noise dressed up as insight.</summary>
    public bool HasComparison => !PreviousSpent.IsZero;

    /// <summary>Nothing happened in either week — the recap should not be shown at all rather than reporting zeros.</summary>
    public bool IsEmpty => Spent.IsZero && PreviousSpent.IsZero && Saved.IsZero;

    /// <summary>Categories touched, largest first (never null — an untouched week is an empty list).</summary>
    public IReadOnlyList<WeeklyRecapSlice> CategoryBreakdown => Categories ?? [];

    /// <summary>Tags used, largest first. Empty is the normal case, not a gap.</summary>
    public IReadOnlyList<WeeklyRecapSlice> TagBreakdown => Tags ?? [];

    /// <summary>True when we have a steady income figure to smooth against — so the modal can label the income tile
    /// as a *typical* week rather than claim money literally arrived.</summary>
    public bool IncomeIsTypical => !string.IsNullOrEmpty(TypicalWeeklyIncome.Currency) && !TypicalWeeklyIncome.IsZero;

    /// <summary>The income figure "left over" is measured against: the steady weekly income when we have one, else
    /// the literal deposits that landed in the covered week. The smoothing is the whole point — salary lands in a
    /// single week, so measuring every other week against its own (zero) deposits reports a loss the user didn't have.</summary>
    public Money EffectiveIncome => IncomeIsTypical ? TypicalWeeklyIncome
        : string.IsNullOrEmpty(Income.Currency) ? Money.Zero(Spent.Currency) : Income;

    /// <summary>What the week left behind: income minus money out. Positive is the good direction. Uses
    /// <see cref="EffectiveIncome"/>, so on a typical-income account a quiet week reads as a small surplus rather
    /// than "you spent and earned nothing".</summary>
    public Money Net => EffectiveIncome - Spent;
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

        // --- The detail behind the card ------------------------------------------------------------------
        var inWeek = expenses.Where(e => e.Date >= from && e.Date <= to).ToList();

        // Carryover is booked as a contribution from a sentinel member; it is last month's leftover, not money
        // that arrived this week. Counting it would report an income spike every time a period rolls over.
        var income = account.Periods
            .SelectMany(p => p.Contributions.Where(c => c.MemberId != Period.CarryoverSource))
            .Where(c => c.Date >= from && c.Date <= to)
            .Aggregate(zero, (acc, c) => acc + c.Paid);

        // Ordered by amount then date so the "biggest" is stable when two expenses tie — otherwise the card
        // could name a different one on every render for no reason the user could see.
        var biggestExpense = inWeek
            .OrderByDescending(e => e.Amount.Amount).ThenBy(e => e.Date).ThenBy(e => e.Id)
            .FirstOrDefault();
        var biggest = biggestExpense is null
            ? null
            : new WeeklyRecapExpense(biggestExpense.Amount, biggestExpense.CategoryId, biggestExpense.Date, biggestExpense.Note);

        var categories = byCategory
            .Select(x => new WeeklyRecapSlice(x.CategoryId, x.Total, inWeek.Count(e => e.CategoryId == x.CategoryId)))
            .ToList();

        // Untagged expenses are simply absent rather than bucketed under an "(untagged)" pseudo-tag: tagging is
        // opt-in and partial by design, so a phantom row would top the list for nearly every account that uses it.
        var tags = inWeek
            .Where(e => e.TagId is not null)
            .GroupBy(e => e.TagId!.Value)
            .Select(g => new WeeklyRecapSlice(g.Key, g.Aggregate(zero, (acc, e) => acc + e.Amount), g.Count()))
            .OrderByDescending(t => t.Total.Amount)
            .ToList();

        // --- Typical weekly income -----------------------------------------------------------------------
        // Salary lands in one week a month, so the literal in-week income is zero three weeks in four and "left over"
        // reads as a loss on every one of them. Smooth it to a steady weekly figure instead, in the user's own
        // priority order:
        //   1. what the account actually earns — the average income of the periods that received any, so a
        //      not-yet-paid current period doesn't drag the figure down (it's excluded, not counted as a zero);
        //   2. failing any income at all (a fresh account), the recurring income they've set up but not yet received;
        //   3. nothing to go on → zero, and Net falls back to the honest in-week cash flow.
        // A flat 52/12 weeks per month keeps this a re-engagement heuristic, not an accounting claim.
        const decimal weeksPerMonth = 52m / 12m;
        var earningMonths = account.Periods
            .Select(p => p.ContributionsPaidTotal.Amount)
            .Where(a => a > 0m)
            .ToList();
        var monthlyIncome = earningMonths.Count > 0
            ? earningMonths.Average()
            : account.RecurringItems.Where(r => r.Kind == RecurringKind.Income).Sum(r => r.ExpectedAmount);
        var typicalWeekly = monthlyIncome > 0m ? decimal.Round(monthlyIncome / weeksPerMonth, 2) : 0m;

        return new WeeklyRecap(from, to, spent, previous, topCategoryId, topSpent, saved,
            inWeek.Count, income, biggest, categories, tags,
            TypicalWeeklyIncome: new Money(typicalWeekly, account.Currency));
    }
}
