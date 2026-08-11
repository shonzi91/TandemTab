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

    /// <summary>
    /// The one category every expense on this trip files into, when the user picks one. Null keeps the original
    /// behaviour: each trip label files into its own everyday category.
    /// <para>
    /// <b>★ Why this exists.</b> Filing a trip across the everyday categories put a Roman hotel in <i>Rent</i> and
    /// a week of restaurants in the <i>Food</i> budget — so a fortnight away detonated three household budgets at
    /// once, and the categories stopped meaning what they say. With one category the trip is a single line in the
    /// month, which is what it actually is, and the everyday budgets keep describing everyday life.
    /// </para>
    /// <para>
    /// <b>Nothing is lost by collapsing them</b>, and that is what makes it safe: the tag axis still carries
    /// stay / travel / food / tickets, so "what did the hotel cost" is answered by the trip's own recap. The
    /// category axis was never where that question was going to be answered — no household has an
    /// "Accommodation" budget.
    /// </para>
    /// <para>Per trip, not global: a road trip you want spread across the usual categories simply leaves it unset.</para>
    /// </summary>
    public Guid? CategoryId { get; private set; }

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

    /// <summary>
    /// The day the traveller declared the trip over, or null while it is simply running its dates out.
    /// <para>
    /// <b>★ "Finish" has to mean OVER, and dates alone can't say that.</b> Pulling <see cref="To"/> back to today
    /// still leaves today inside the trip, so the app went on wearing it — the Home hero stayed up, the expense form
    /// went on defaulting to it, and the card still read "Day 4". Someone who has just landed and tapped Finish is
    /// telling us something the calendar doesn't know.
    /// </para>
    /// <para>
    /// It is a date rather than a flag because the date is the fact ("we got back on the 11th"), and because a flag
    /// would have nothing to say if the trip were ever reopened and finished again.
    /// </para>
    /// </summary>
    public DateOnly? FinishedOn { get; private set; }

    /// <summary>
    /// The day the traveller confirmed they had actually left, or null while the trip is only <i>due</i> to start.
    /// <para>
    /// <b>★ Trip mode is opt-in on the day, not automatic.</b> A date is not a departure: the flight may be at six in
    /// the evening, it may be delayed, the whole thing may be called off. Switching the app's shell, its Home card
    /// and its expense-form default on the stroke of midnight means every coffee bought on the morning of departure
    /// is filed as holiday spending, and the user never asked for that.
    /// </para>
    /// <para>
    /// So the date opens a <i>window</i> (<see cref="IsAwaitingStart"/>) and the user closes it with one tap. Nothing
    /// is lost by waiting: an expense can be attached to the trip either way, and the trip is still offered by the
    /// entry form the whole time.
    /// </para>
    /// </summary>
    public DateOnly? StartedOn { get; private set; }

    /// <summary>
    /// How much of the linked savings bucket has been released into this trip's budget — the running total of
    /// "we saved for this, and now we're spending it".
    /// <para>
    /// Separate from <c>TripRecap.FundedFromSavings</c>, which counts expenses paid <i>directly</i> out of the
    /// bucket. Both are true and they answer different questions: this one is the money moved across in advance,
    /// that one is the money taken out as it was spent. Neither discounts the trip's total — the money left either
    /// way.
    /// </para>
    /// </summary>
    public decimal SavingsApplied { get; private set; }

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

    /// <summary>Set the one category this trip's expenses file into, or clear it with null/empty to go back to
    /// per-label filing. Validated by <c>Account.SetTripCategory</c>, which owns the category list.</summary>
    public void SetCategory(Guid? categoryId) =>
        CategoryId = categoryId is { } id && id != Guid.Empty ? id : null;

    /// <summary>True when this trip collapses into one category — i.e. the expense form should default there and
    /// a trip label should tag without re-filing.</summary>
    public bool FilesIntoOneCategory => CategoryId is not null;

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

    /// <summary>Confirm the trip has actually begun — the tap that turns trip mode on. Idempotent, and it never
    /// touches the dates: the plan said the 8th and the plan is what the recap's "booked ahead" split is measured
    /// against, so a confirmation at six in the evening must not silently re-file the morning's bookings.</summary>
    public void Start(DateOnly today) => StartedOn ??= today;

    /// <summary>Undo a <see cref="Start"/> — "no, we haven't left yet".</summary>
    public void Unstart() => StartedOn = null;

    /// <summary>
    /// End the trip as of <paramref name="today"/>: it stops on the day it actually stopped, and it is over from
    /// that moment rather than at midnight. Pulls <see cref="To"/> in when the trip was going to run longer, and
    /// never pushes it out — finishing early is what this is for; extending a trip is an edit.
    /// </summary>
    public void Finish(DateOnly today)
    {
        // A trip cannot end before it starts, so finishing one that hasn't left yet leaves the dates alone and
        // simply marks it done — "we cancelled it" and "we came back" are the same fact to everything downstream.
        if (today < To && today >= From) To = today;
        FinishedOn = today;
    }

    /// <summary>Undo a <see cref="Finish"/> — the dates stand, the trip is simply live again. The pulled-in end date
    /// is deliberately NOT restored: we no longer know what it was, and inventing one would be worse than leaving
    /// the user to set it.</summary>
    public void Reopen() => FinishedOn = null;

    /// <summary>Record money released from the linked savings bucket into this trip's budget. Additive — the flow
    /// is "release some now, some more later", and a setter would lose the earlier releases.</summary>
    public void AddSavingsApplied(decimal amount)
    {
        if (amount > 0m) SavingsApplied += amount;
    }

    /// <summary>Clear the released-savings total (the undo path).</summary>
    public void ResetSavingsApplied() => SavingsApplied = 0m;

    /// <summary>How many days the trip covers, both ends inclusive.</summary>
    public int LengthInDays => To.DayNumber - From.DayNumber + 1;

    // ★ Every "is the app wearing this trip" question routes through these — the shell theme, the Home hero, the
    // expense form's default and the card's status all derive from them — so honouring StartedOn/FinishedOn here is
    // the whole of "opt in on the day" and "finish means over". There is nowhere else to remember to check them.
    //
    // The four states are: upcoming (before the dates) → awaiting start (dates open, not confirmed) → active
    // (confirmed) → finished. Awaiting-start is deliberately NOT active: see StartedOn.
    public bool IsActiveOn(DateOnly today) => FinishedOn is null && StartedOn is not null && today >= From && today <= To;
    // Deliberately NOT gated on StartedOn: a trip can only be started once its window is open, so before From it is
    // upcoming whatever else is set — and a state that is neither upcoming nor active nor finished is a limbo the
    // UI would have nothing to say about.
    public bool IsUpcomingOn(DateOnly today) => FinishedOn is null && today < From;
    public bool IsFinishedOn(DateOnly today) => FinishedOn is not null || today > To;

    /// <summary>The trip's dates have arrived but nobody has said they've left yet — the state the "Start the trip?"
    /// prompt lives in. Neither upcoming nor running: it is a question waiting for an answer.</summary>
    public bool IsAwaitingStart(DateOnly today) =>
        FinishedOn is null && StartedOn is null && today >= From && today <= To;

    /// <summary>Which day of the trip it is ("Day 4"), or null when the trip isn't running today.</summary>
    public int? DayOn(DateOnly today) => IsActiveOn(today) ? today.DayNumber - From.DayNumber + 1 : null;

    /// <summary>Sleeps until departure, or null once it has started. Zero means it starts today.</summary>
    public int? DaysUntil(DateOnly today) => IsUpcomingOn(today) ? From.DayNumber - today.DayNumber : null;

    private static string Clean(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Trip name is required.", nameof(name))
            : name.Trim();
}
