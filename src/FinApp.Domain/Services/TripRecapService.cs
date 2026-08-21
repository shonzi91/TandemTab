using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Domain.Services;

/// <summary>One label's share of a trip — used for both the category and the tag splits.</summary>
public sealed record TripRecapSlice(Guid Id, Money Total, int Count);

/// <summary>The single largest thing bought on a trip — usually the flight or the hotel, and usually the line
/// someone actually remembers. <paramref name="PaidFromAccountId"/> names its account when it was paid from
/// another one, so a flight bought on a joint card doesn't read as this account's.</summary>
public sealed record TripRecapExpense(Money Amount, Guid CategoryId, DateOnly Date, string? Note,
    Guid? PaidFromAccountId = null);

/// <summary>
/// One expense gathered from <b>another</b> account onto this trip (D1).
/// <para>
/// ★ It carries its own resolved names because ids minted in another account resolve to nothing here: a category
/// looked up in the wrong account comes back null and the slice renders as "—", which is a breakdown lying with a
/// straight face. The names travel with the row rather than being fetched later, so a recap is always
/// self-describing.
/// </para>
/// </summary>
public sealed record ForeignTripExpense(Guid AccountId, string AccountName, Expense Expense,
    string CategoryName, string? TagName);

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
/// <param name="PaidFromOtherAccounts">How much of <paramref name="Spent"/> was paid from a <b>different</b>
/// account (D1). ⚠️ Counted in the total AND reported separately — the same discipline
/// <paramref name="FundedFromSavings"/> follows, and for a sharper reason: a total that quietly includes another
/// household's card is a figure nobody can reconcile against their own ledger. Zero on an ordinary trip.</param>
/// <param name="BySourceAccount">What each account contributed, largest first. Empty on an ordinary trip.</param>
/// <param name="ForeignNames">Display names for ids that came from another account — categories, tags and the
/// accounts themselves. See <see cref="TripRecap.ForeignName"/>.</param>
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
    Money Untagged = default,
    Money PaidFromOtherAccounts = default,
    IReadOnlyList<TripRecapSlice>? BySourceAccount = null,
    IReadOnlyDictionary<Guid, string>? ForeignNames = null)
{
    /// <summary>What each contributing account put in, largest first — including this one, so the row the reader
    /// is standing in is in the same list as the others. Empty unless another account has spend on this trip.</summary>
    public IReadOnlyList<TripRecapSlice> SourceAccountBreakdown => BySourceAccount ?? [];

    /// <summary>True when spend from another account is folded into <see cref="Spent"/>. The caller MUST say so —
    /// see <see cref="PaidFromOtherAccounts"/>.</summary>
    public bool HasOtherAccountSpend => PaidFromOtherAccounts.Amount > 0m;

    /// <summary>The name of a category, tag or account id that came from another account, or null when the id is
    /// this account's own (and therefore resolvable the usual way). ⚠️ Callers rendering a slice must fall back to
    /// this before printing a placeholder, or every foreign slice reads as "—".</summary>
    public string? ForeignName(Guid id) =>
        ForeignNames is { } names && names.TryGetValue(id, out var name) ? name : null;

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
    /// <param name="foreign">Expenses in OTHER accounts attached to this trip (D1). ★ A trailing optional rather
    /// than a set of accounts: every existing caller stays valid, and <c>Build</c> never gains the ability to reach
    /// into another account's periods for itself — it is handed exactly the rows it may see, already named.</param>
    /// <param name="ownAccountId">Which id to label this account's own share with in
    /// <see cref="TripRecap.BySourceAccount"/>. ⚠️ Defaults to the aggregate's own <c>Id</c>, but the server passes
    /// the ROUTE's id: the aggregate's comes out of the payload, and every cross-account link is written with the
    /// route id. Mismatched, this account's own slice fails to match itself and renders as an unknown one.</param>
    public TripRecap? Build(Account account, Guid tripId, IReadOnlyList<ForeignTripExpense>? foreign = null,
        Guid? ownAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var trip = account.FindTrip(tripId);
        if (trip is null) return null;

        var zero = Money.Zero(account.Currency);
        var own = account.TripExpenses(tripId).ToList();

        // ⚠️ Same currency only, checked here as well as at the attach. Money's + throws on a mismatch, and this
        // sum feeds the whole Trips screen on the server — one stray row of another currency would take the page
        // down rather than render oddly. Belt and braces: the write gate is the real fix, this is the blast radius.
        var foreignRows = (foreign ?? [])
            .Where(f => string.Equals(f.Expense.Amount.Currency, account.Currency, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // One list for every figure the recap reports, because the trip cost what it cost regardless of which card
        // paid — the WHERE-FROM is reported alongside (PaidFromOtherAccounts), never by leaving rows out.
        var expenses = own.Concat(foreignRows.Select(f => f.Expense))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime)
            .ToList();

        Money Sum(IEnumerable<Expense> src) => src.Aggregate(zero, (acc, e) => acc + e.Amount);

        var spent = Sum(expenses);
        var paidFromOthers = Sum(foreignRows.Select(f => f.Expense));

        // Savings-funded rows are counted in the total AND reported separately: the money was genuinely spent, it
        // just came out of a bucket that had been filling for it. Reporting only one of the two figures makes the
        // trip look either free or unfunded.
        // ⚠️ `own` only, and written out rather than left to luck: SourceSavingCategoryId on a foreign row names a
        // bucket in THAT account, which this trip's bucket is not — today the ids simply never match, but resting a
        // money figure on two Guids not colliding is not a rule anyone can check later.
        var fundedFromSavings = trip.SavingCategoryId is { } bucketId
            ? Sum(own.Where(e => e.SourceSavingCategoryId == bucketId))
            : zero;

        // Names for everything that came from elsewhere, so no slice has to render a placeholder — plus which
        // account each foreign row came from, for the rows the recap names individually.
        var foreignNames = new Dictionary<Guid, string>();
        var sourceAccountOf = new Dictionary<Guid, Guid>();
        foreach (var f in foreignRows)
        {
            foreignNames[f.AccountId] = f.AccountName;
            sourceAccountOf[f.Expense.Id] = f.AccountId;
            foreignNames.TryAdd(f.Expense.CategoryId, f.CategoryName);
            if (f.Expense.TagId is { } ftag && f.TagName is { } ftagName) foreignNames.TryAdd(ftag, ftagName);
        }

        // Who paid what. This account is in the list too — a breakdown that names only the others would leave the
        // reader working out their own share by subtraction.
        var bySourceAccount = foreignRows.Count == 0
            ? []
            : foreignRows.GroupBy(f => f.AccountId)
                .Select(g => new TripRecapSlice(g.Key, Sum(g.Select(f => f.Expense)), g.Count()))
                .Append(new TripRecapSlice(ownAccountId ?? account.Id, Sum(own), own.Count))
                .Where(s => s.Count > 0)
                .OrderByDescending(s => s.Total.Amount)
                .ToList();

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
                // ⚠️ Names its account when it isn't ours. "Biggest: €480, flight" rendered unlabelled reads as
                // this account having spent €480 it never did — and the biggest line on a shared trip is exactly
                // the one most likely to have been put on someone else's card.
                : new TripRecapExpense(biggestExpense.Amount, biggestExpense.CategoryId, biggestExpense.Date, biggestExpense.Note,
                    sourceAccountOf.TryGetValue(biggestExpense.Id, out var biggestFrom) ? biggestFrom : null),
            Categories: categories,
            Tags: tags,
            Untagged: Sum(expenses.Where(e => e.TagId is null)),
            PaidFromOtherAccounts: paidFromOthers,
            BySourceAccount: bySourceAccount,
            ForeignNames: foreignNames.Count == 0 ? null : foreignNames);
    }

    /// <summary>Every trip's recap, newest departure first — the trips list.</summary>
    /// <param name="foreignByTrip">Other accounts' expenses, keyed by trip id (D1). Absent for an account nobody
    /// has shared a trip into, which is every account until someone does.</param>
    public IReadOnlyList<TripRecap> BuildAll(Account account,
        IReadOnlyDictionary<Guid, IReadOnlyList<ForeignTripExpense>>? foreignByTrip = null, Guid? ownAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.TripsByDeparture
            .Select(t => Build(account, t.Id,
                foreignByTrip is { } map && map.TryGetValue(t.Id, out var rows) ? rows : null, ownAccountId))
            .OfType<TripRecap>()
            .ToList();
    }
}
