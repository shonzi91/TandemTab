using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Domain.Services;

/// <summary>One label's share of a trip — used for both the category and the tag splits.</summary>
public sealed record TripRecapSlice(Guid Id, Money Total, int Count);

/// <summary>The single largest thing bought on a trip — usually the flight or the hotel, and usually the line
/// someone actually remembers.</summary>
public sealed record TripRecapExpense(Money Amount, Guid CategoryId, DateOnly Date, string? Note);

/// <summary>
/// What a trip cost, and how it broke down. Computed from the expenses <i>linked</i> to the trip, never from a date
/// window — see <see cref="Trip"/>.
/// </summary>
/// <param name="TripId">The trip described.</param>
/// <param name="Name">Its name, so the caller can render without a second lookup.</param>
/// <param name="From">First day of the trip, inclusive.</param>
/// <param name="To">Last day of the trip, inclusive.</param>
/// <param name="Spent">Everything attached to the trip, whenever it was paid.</param>
/// <param name="ExpenseCount">How many separate expenses were attached.</param>
/// <param name="PrePaid">The part paid <b>before</b> departure — flights, hotels, tours. Worth its own figure
/// because it is the half people forget: the trip felt like it cost what they spent while away.</param>
/// <param name="OnTrip">The part paid between the dates.</param>
/// <param name="AfterReturn">The part paid after getting home — a late card charge, a fine, a settled bill.</param>
/// <param name="FundedFromSavings">How much of <paramref name="Spent"/> was drawn from the trip's linked savings
/// bucket. Zero when no bucket is linked, which is the ordinary case.</param>
/// <param name="Budget">What it was expected to cost, when a figure was set.</param>
/// <param name="Biggest">The single largest expense, or null when nothing is attached yet.</param>
/// <param name="Categories">Every category touched, largest first.</param>
/// <param name="Tags">Every tag used, largest first — the trip split (stay / travel / food / tickets).</param>
/// <param name="Untagged">The spend carrying no tag at all. Not a slice: it's the measure of how much the tag
/// split is worth showing.</param>
public sealed record TripRecap(
    Guid TripId,
    string Name,
    DateOnly From,
    DateOnly To,
    Money Spent,
    int ExpenseCount = 0,
    Money PrePaid = default,
    Money OnTrip = default,
    Money AfterReturn = default,
    Money FundedFromSavings = default,
    decimal? Budget = null,
    TripRecapExpense? Biggest = null,
    IReadOnlyList<TripRecapSlice>? Categories = null,
    IReadOnlyList<TripRecapSlice>? Tags = null,
    Money Untagged = default)
{
    /// <summary>Categories touched, largest first (never null — an empty trip is an empty list).</summary>
    public IReadOnlyList<TripRecapSlice> CategoryBreakdown => Categories ?? [];

    /// <summary>Tags used, largest first (never null).</summary>
    public IReadOnlyList<TripRecapSlice> TagBreakdown => Tags ?? [];

    /// <summary>Days the trip covers, both ends inclusive.</summary>
    public int LengthInDays => To.DayNumber - From.DayNumber + 1;

    /// <summary>Average spend per day of the trip. Uses the trip's own length, not the span of the expense dates —
    /// a booking made in March must not stretch the denominator to four months and report a daily cost of nothing.</summary>
    public Money PerDay => new(Spent.Amount / LengthInDays, Spent.Currency);

    public bool HasBudget => Budget is > 0m;

    /// <summary>Over (positive) or under (negative) the budget, or null when none was set.</summary>
    public Money? AgainstBudget => Budget is { } b && b > 0m ? new Money(Spent.Amount - b, Spent.Currency) : null;

    public bool IsOverBudget => AgainstBudget is { } d && d.Amount > 0m;

    /// <summary>Nothing has been attached yet — the recap should offer an empty state, not a page of zeros.</summary>
    public bool IsEmpty => ExpenseCount == 0;

    /// <summary>
    /// True when enough of the trip is tagged for the tag split to be the honest headline. Below half, the pie
    /// would be mostly one unlabelled hole, so the caller should lead with categories instead — the axis every
    /// expense always carries.
    /// </summary>
    public bool TagsAreRepresentative =>
        Spent.Amount > 0m && (Spent.Amount - Untagged.Amount) / Spent.Amount >= 0.5m;
}

/// <summary>
/// Builds a <see cref="TripRecap"/> — "what did Rome cost?".
/// <para>
/// <b>Expenses are gathered by their trip link across every period</b>, which is the whole point: the flight sits in
/// March, the hotel in April and the coffees in June, and any recap scoped to a single period would answer a
/// question nobody asked. The <i>trip's</i> dates are used only to say which part of the total was paid before
/// leaving, while away, and after getting back.
/// </para>
/// <para>
/// The same builder serves a finished trip, a trip underway ("so far") and one that hasn't started (only the
/// bookings). There is no separate "live" recap — a trip in progress is simply one whose expenses are still arriving.
/// </para>
/// </summary>
public sealed class TripRecapService
{
    /// <summary>Build the recap for one trip, or null when no such trip exists in the account.</summary>
    public TripRecap? Build(Account account, Guid tripId)
    {
        ArgumentNullException.ThrowIfNull(account);
        var trip = account.FindTrip(tripId);
        if (trip is null) return null;

        var zero = Money.Zero(account.Currency);
        var expenses = account.TripExpenses(tripId).ToList();

        Money Sum(IEnumerable<Expense> src) => src.Aggregate(zero, (acc, e) => acc + e.Amount);

        var spent = Sum(expenses);

        // Savings-funded rows are counted in the total AND reported separately: the money was genuinely spent, it
        // just came out of a bucket that had been filling for it. Reporting only one of the two figures makes the
        // trip look either free or unfunded.
        var fundedFromSavings = trip.SavingCategoryId is { } bucketId
            ? Sum(expenses.Where(e => e.SourceSavingCategoryId == bucketId))
            : zero;

        var biggestExpense = expenses
            // Amount, then date, then id — a stable order, so the card can't name a different "biggest" on every
            // render when two expenses tie.
            .OrderByDescending(e => e.Amount.Amount).ThenBy(e => e.Date).ThenBy(e => e.Id)
            .FirstOrDefault();

        var categories = expenses
            .GroupBy(e => e.CategoryId)
            .Select(g => new TripRecapSlice(g.Key, Sum(g), g.Count()))
            .OrderByDescending(s => s.Total.Amount)
            .ToList();

        // Untagged rows are left out of the slices rather than bucketed under a phantom "(untagged)" tag, exactly as
        // the weekly recap does — but their total is carried alongside, because for a trip the share that went
        // untagged decides whether this split is worth leading with at all.
        var tags = expenses
            .Where(e => e.TagId is not null)
            .GroupBy(e => e.TagId!.Value)
            .Select(g => new TripRecapSlice(g.Key, Sum(g), g.Count()))
            .OrderByDescending(s => s.Total.Amount)
            .ToList();

        return new TripRecap(
            trip.Id, trip.Name, trip.From, trip.To,
            spent,
            expenses.Count,
            PrePaid: Sum(expenses.Where(e => e.Date < trip.From)),
            OnTrip: Sum(expenses.Where(e => e.Date >= trip.From && e.Date <= trip.To)),
            AfterReturn: Sum(expenses.Where(e => e.Date > trip.To)),
            FundedFromSavings: fundedFromSavings,
            Budget: trip.Budget,
            Biggest: biggestExpense is null
                ? null
                : new TripRecapExpense(biggestExpense.Amount, biggestExpense.CategoryId, biggestExpense.Date, biggestExpense.Note),
            Categories: categories,
            Tags: tags,
            Untagged: Sum(expenses.Where(e => e.TagId is null)));
    }

    /// <summary>Every trip's recap, newest departure first — the trips list.</summary>
    public IReadOnlyList<TripRecap> BuildAll(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.TripsByDeparture
            .Select(t => Build(account, t.Id))
            .OfType<TripRecap>()
            .ToList();
    }
}
