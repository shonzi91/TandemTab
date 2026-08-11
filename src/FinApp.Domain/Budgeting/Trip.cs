using FinApp.Domain.Common;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// A named journey an expense can be attached to — the unit "how much did Rome cost?" is asked about.
/// <para>
/// <b>★ Membership is by link, not by date.</b> An expense belongs to a trip because it carries
/// <see cref="Budgeting.Expense.TripId"/>, never because its date happens to fall inside
/// <see cref="From"/>..<see cref="To"/>. That is the whole design: a flight booked three months early keeps its own
/// date, its own period and its own budget impact, and still counts toward the trip total. The dates say when the
/// trip *is* (so the app can default the link while you're away, and count down to it) — they never define what's in it.
/// </para>
/// <para>
/// Body data — travels in the account snapshot, not the relational header, so adding trips needs no migration.
/// </para>
/// </summary>
public sealed class Trip : Entity
{
    /// <summary>What it's called — "Rome", "Christmas at home", "Ski week".</summary>
    public string Name { get; private set; }

    /// <summary>Where it was, free text ("Rome, Italy"). Optional and purely descriptive: nothing resolves it to a
    /// place, so it can be a city, a country, or "road trip" without the model caring.</summary>
    public string? Destination { get; private set; }

    /// <summary>First day of the trip, inclusive.</summary>
    public DateOnly From { get; private set; }

    /// <summary>Last day of the trip, inclusive — so a single-day trip has <see cref="From"/> == <see cref="To"/>
    /// and a length of one, not zero.</summary>
    public DateOnly To { get; private set; }

    /// <summary>Optional display icon (emoji — typically a flag). Null means the UI derives one.</summary>
    public string? Icon { get; private set; }

    /// <summary>The savings bucket this trip is funded from, when there is one. A link only — no money moves
    /// through it. It lets the recap say "€1,500 of this came out of the Holiday fund", which is the figure that
    /// turns a total into an answer. Null is the normal case for a trip paid out of ordinary income.</summary>
    public Guid? SavingCategoryId { get; private set; }

    /// <summary>What the trip was expected to cost, when the user set a figure. Optional: a trip with no budget
    /// still reports its total, it just has nothing to be over or under.</summary>
    public decimal? Budget { get; private set; }

    /// <summary>The currency actually being spent on the trip, when it isn't the account's ("GBP"). Display only —
    /// see <see cref="Rate"/>.</summary>
    public string? SpendCurrency { get; private set; }

    /// <summary>How many units of the <i>account</i> currency one unit of <see cref="SpendCurrency"/> is worth
    /// (1 GBP = 1.17 EUR → <c>1.17</c>). One fixed rate the user sets for the whole trip; deliberately not a live
    /// feed, which would mean a rate provider, a new sub-processor and a rate per expense.
    /// <para>
    /// <b>★ A conversion at entry time, never a rule applied to stored rows</b> — the same discipline as a tag's
    /// category binding. Amounts are stored converted, in the account currency, so every total in the app stays
    /// in one unit and editing the rate later cannot silently rewrite what past expenses cost.
    /// </para></summary>
    public decimal? Rate { get; private set; }

    public Trip(string name, DateOnly from, DateOnly to)
    {
        Name = Clean(name);
        if (to < from)
            throw new ArgumentException("A trip cannot end before it starts.", nameof(to));
        From = from;
        To = to;
    }

    /// <summary>Rename, re-date and re-describe in one call — the shape the edit form posts.</summary>
    public void Update(string name, DateOnly from, DateOnly to, string? destination, string? icon)
    {
        if (to < from)
            throw new ArgumentException("A trip cannot end before it starts.", nameof(to));
        Name = Clean(name);
        From = from;
        To = to;
        SetDestination(destination);
        SetIcon(icon);
    }

    public void SetDestination(string? destination) =>
        Destination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();

    public void SetIcon(string? icon) => Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    /// <summary>Link this trip to the savings bucket it's funded from (or clear with null/empty). Validated by
    /// <c>Account.SetTripSavingCategory</c>, which owns the bucket list.</summary>
    public void SetSavingCategory(Guid? savingCategoryId) =>
        SavingCategoryId = savingCategoryId is { } id && id != Guid.Empty ? id : null;

    /// <summary>Set (or clear with null) what the trip is expected to cost. A negative budget is meaningless, so
    /// it clears rather than throwing — the field is a planning aid, not a ledger entry.</summary>
    public void SetBudget(decimal? budget) => Budget = budget is > 0m ? budget : null;

    /// <summary>Set the fixed conversion for a trip spent in another currency, or clear it by passing null for
    /// either half. The two only mean anything together: a rate with no currency has nothing to label the field
    /// with, and a currency with no rate cannot convert.</summary>
    public void SetRate(string? spendCurrency, decimal? rate)
    {
        if (string.IsNullOrWhiteSpace(spendCurrency) || rate is not > 0m)
        {
            SpendCurrency = null;
            Rate = null;
            return;
        }
        SpendCurrency = spendCurrency.Trim().ToUpperInvariant();
        Rate = rate;
    }

    /// <summary>True when this trip converts — i.e. the add-expense form should offer the second currency.</summary>
    public bool HasRate => Rate is > 0m && !string.IsNullOrEmpty(SpendCurrency);

    /// <summary>Convert an amount typed in <see cref="SpendCurrency"/> into the account currency, rounded to the
    /// cent. Returns the amount unchanged when no rate is set, so callers need no branch.</summary>
    public decimal ToAccountCurrency(decimal amountInSpendCurrency) =>
        Rate is { } r && r > 0m
            ? decimal.Round(amountInSpendCurrency * r, 2, MidpointRounding.AwayFromZero)
            : amountInSpendCurrency;

    /// <summary>How many days the trip covers, both ends inclusive.</summary>
    public int LengthInDays => To.DayNumber - From.DayNumber + 1;

    public bool IsActiveOn(DateOnly today) => today >= From && today <= To;
    public bool IsUpcomingOn(DateOnly today) => today < From;
    public bool IsFinishedOn(DateOnly today) => today > To;

    /// <summary>Which day of the trip it is ("Day 4"), or null when the trip isn't running today.</summary>
    public int? DayOn(DateOnly today) => IsActiveOn(today) ? today.DayNumber - From.DayNumber + 1 : null;

    /// <summary>Sleeps until departure, or null once it has started. Zero means it starts today.</summary>
    public int? DaysUntil(DateOnly today) => IsUpcomingOn(today) ? From.DayNumber - today.DayNumber : null;

    private static string Clean(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Trip name is required.", nameof(name))
            : name.Trim();
}
