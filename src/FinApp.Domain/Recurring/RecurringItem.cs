using FinApp.Domain.Common;

namespace FinApp.Domain.Recurring;

/// <summary>Whether a recurring item is money going out (a bill) or coming in (salary, regular income).</summary>
public enum RecurringKind { Expense, Income }

/// <summary>How the amount is treated. <see cref="Fixed"/> is the same every time (rent, subscriptions);
/// <see cref="Typical"/> is an estimate that self-tunes toward what you actually pay (utilities); and
/// <see cref="ReminderOnly"/> claims no amount at all — it just prompts you to enter the real figure (variable salary).</summary>
public enum RecurringAmountMode { Fixed, Typical, ReminderOnly }

/// <summary>
/// A recurring <b>expectation</b> — a bill, salary or standing transfer that repeats monthly. It is a template that
/// predicts an item, <b>not</b> an auto-posted transaction: each period it becomes "due" on <see cref="DayOfMonth"/>,
/// and the user confirms the real amount (which posts a normal expense/contribution). This is what lets recurring
/// items handle amounts that vary — the estimate is only a hint until confirmed.
/// <para>Body data — travels in the account snapshot, so no server/schema change and it's ignored cleanly by an
/// older client if the feature is rolled back.</para>
/// </summary>
public sealed class RecurringItem : Entity
{
    public string Name { get; private set; }
    public string? Icon { get; private set; }
    public RecurringKind Kind { get; private set; }
    public RecurringAmountMode AmountMode { get; private set; }

    /// <summary>Expected amount for <see cref="RecurringAmountMode.Fixed"/>/<see cref="RecurringAmountMode.Typical"/>
    /// (0 and unused for <see cref="RecurringAmountMode.ReminderOnly"/>). Typical self-tunes on each confirm.</summary>
    public decimal ExpectedAmount { get; private set; }

    /// <summary>
    /// Day of the month it's expected (1–31). Days past the end of a short month are pulled back to its last day by
    /// <see cref="DueDateWithin"/>, which is why the range can be the full 1–31 rather than the old 1–28: a loan
    /// stating the 30th has to be storable as the 30th, since <see cref="Savings.SavingCategory.DebtInstallmentDay"/>
    /// already allows it and a bill that services that loan must be able to agree with it.
    /// </summary>
    public int DayOfMonth { get; private set; }

    /// <summary>Expense category (for a bill) or contribution category (for income).</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>The fund the money moves out of / into.</summary>
    public Guid FundId { get; private set; }

    public bool Active { get; private set; } = true;

    /// <summary>Post automatically (with the fixed amount) when due, without asking. Only meaningful for
    /// <see cref="RecurringAmountMode.Fixed"/> — a varying/unknown amount always needs confirming, so this is forced
    /// off for the other modes.</summary>
    public bool AutoPost { get; private set; }

    /// <summary>The <c>From</c> date of the period this item was last handled (posted or skipped) — so it only goes
    /// "due" once per period. Null until first handled.</summary>
    public DateOnly? LastHandledPeriodFrom { get; private set; }

    /// <summary>
    /// Whether the last handling was a <b>skip</b> rather than a posting. The two are indistinguishable in
    /// <see cref="LastHandledPeriodFrom"/> alone, and they must not be: un-handling a skip is harmless, while
    /// un-handling a posting would make the item due again with its expense already on the ledger — one bill, paid
    /// twice. So only a skip can be undone, and this is what says which it was.
    /// <para>Body data (snapshot, not EF). False on every item stored before this existed, which reads as "posted" —
    /// the conservative default, since it merely withholds an undo rather than offering an unsafe one.</para>
    /// </summary>
    public bool LastHandledWasSkip { get; private set; }

    /// <summary>Skipped — deliberately not paid — in the period starting <paramref name="periodFrom"/>.</summary>
    public bool SkippedIn(DateOnly periodFrom) => LastHandledPeriodFrom == periodFrom && LastHandledWasSkip;

    public RecurringItem(string name, RecurringKind kind, RecurringAmountMode amountMode, decimal expectedAmount,
        int dayOfMonth, Guid categoryId, Guid fundId, string? icon = null, bool autoPost = false)
    {
        Name = Clean(name);
        Kind = kind;
        AmountMode = amountMode;
        ExpectedAmount = amountMode == RecurringAmountMode.ReminderOnly ? 0m : Math.Max(0m, expectedAmount);
        DayOfMonth = Math.Clamp(dayOfMonth, 1, 31);
        CategoryId = categoryId;
        FundId = fundId;
        Icon = icon;
        AutoPost = autoPost && amountMode == RecurringAmountMode.Fixed;
    }

    public void Update(string name, RecurringAmountMode amountMode, decimal expectedAmount, int dayOfMonth,
        Guid categoryId, Guid fundId, string? icon, bool autoPost = false)
    {
        Name = Clean(name);
        AmountMode = amountMode;
        ExpectedAmount = amountMode == RecurringAmountMode.ReminderOnly ? 0m : Math.Max(0m, expectedAmount);
        DayOfMonth = Math.Clamp(dayOfMonth, 1, 31);
        CategoryId = categoryId;
        FundId = fundId;
        Icon = icon;
        AutoPost = autoPost && amountMode == RecurringAmountMode.Fixed;
    }

    public void SetActive(bool active) => Active = active;

    /// <summary>Move the expected day without touching anything else — used when a linked loan's own installment day
    /// takes over (see <see cref="LinkedDebtBucketId"/>), where re-running <see cref="Update"/> would mean restating
    /// every other field just to change a date.</summary>
    public void SetDayOfMonth(int dayOfMonth) => DayOfMonth = Math.Clamp(dayOfMonth, 1, 31);

    /// <summary>Mark handled for the period starting <paramref name="periodFrom"/>, so it isn't "due" again until the
    /// next period. Pass <paramref name="skipped"/> when nothing was posted — that is the only case
    /// <see cref="ClearHandled"/> will undo.</summary>
    public void MarkHandled(DateOnly periodFrom, bool skipped = false)
    {
        LastHandledPeriodFrom = periodFrom;
        LastHandledWasSkip = skipped;
    }

    /// <summary>Undo a skip: the item falls due again in the period it was skipped in. Refuses on a posting, because
    /// the money has already moved and re-arming the item would invite a second one.</summary>
    public void ClearHandled()
    {
        if (!LastHandledWasSkip)
            throw new InvalidOperationException("Only a skipped item can be un-skipped — this one was posted.");
        LastHandledPeriodFrom = null;
        LastHandledWasSkip = false;
    }

    /// <summary>Nudge a <see cref="RecurringAmountMode.Typical"/> estimate halfway toward what was actually paid, so
    /// next month's prediction improves on its own. No-op for the other modes.</summary>
    public void LearnFromActual(decimal actual)
    {
        if (AmountMode == RecurringAmountMode.Typical && actual > 0m)
            ExpectedAmount = decimal.Round((ExpectedAmount + actual) / 2m, 2);
    }

    /// <summary>The concrete due date inside a period — its <see cref="DayOfMonth"/> in the period's month, clamped
    /// into the [from, to] range so it always lands within the period.</summary>
    public DateOnly DueDateWithin(DateOnly from, DateOnly to)
    {
        var day = Math.Min(DayOfMonth, DateTime.DaysInMonth(from.Year, from.Month));
        var candidate = new DateOnly(from.Year, from.Month, day);
        if (candidate < from) candidate = from;
        if (candidate > to) candidate = to;
        return candidate;
    }

    /// <summary>
    /// When this item was set up. An item never falls due for a date that precedes it: adding "rent, day 10" on the
    /// 19th describes an arrangement going forward, not a payment you forgot to log on the 10th — and with
    /// <see cref="AutoPost"/> on, treating it as due would silently post an expense for a date already gone.
    /// <para>Null on items created before this was tracked; those keep the old behaviour rather than being
    /// retro-dated, since there's no honest value to invent for them.</para>
    /// </summary>
    public DateOnly? CreatedOn { get; private set; }

    /// <summary>Set the creation date (restore path — the serializer replays the stored value verbatim).</summary>
    public void SetCreatedOn(DateOnly? createdOn) => CreatedOn = createdOn;

    /// <summary>
    /// The debt bucket this bill services, when it's a loan installment rather than an ordinary expense. Set, and
    /// posting it splits the payment into interest / principal rows against that loan instead of booking one lump
    /// expense — so the monthly bill you'd already set up becomes the thing that tracks the debt.
    /// <para>
    /// Only meaningful on a <see cref="RecurringKind.Expense"/>; income has no loan to service. Null on every item
    /// created before this existed, which is exactly the old behaviour. Body data (snapshot, not EF).
    /// </para>
    /// </summary>
    public Guid? LinkedDebtBucketId { get; private set; }

    /// <summary>Link this bill to a debt bucket (or unlink with null / <see cref="Guid.Empty"/>). Ignored for income —
    /// there is no installment to split.</summary>
    public void SetLinkedDebtBucket(Guid? bucketId) =>
        LinkedDebtBucketId = Kind == RecurringKind.Expense && bucketId is { } b && b != Guid.Empty ? b : null;

    /// <summary>True when posting this item should log an installment rather than a plain expense.</summary>
    public bool IsLoanInstallment => LinkedDebtBucketId is not null;

    /// <summary>Due (and not yet handled) within period [from, to] as of <paramref name="today"/> — i.e. its day has
    /// arrived, it hasn't been posted or skipped this period, and the due date isn't earlier than
    /// <see cref="CreatedOn"/>.</summary>
    public bool IsDue(DateOnly from, DateOnly to, DateOnly today) =>
        Active && LastHandledPeriodFrom != from && today >= DueDateWithin(from, to) && HasStartedBy(from, to);

    /// <summary>False when this period's due date falls before the item existed — the first period is skipped and it
    /// begins with the next one.</summary>
    private bool HasStartedBy(DateOnly from, DateOnly to) =>
        CreatedOn is not { } created || DueDateWithin(from, to) >= created;

    /// <summary>Active and not yet handled this period (whether or not its day has arrived) — i.e. still expected.
    /// An item whose day already passed before it was created isn't expected this period, so it doesn't nag either.
    /// <para>The [from, to] overload is the accurate one; the single-argument form can't tell whether the item had
    /// started, so it's kept only for callers that have no period range to hand.</para></summary>
    public bool IsPending(DateOnly from) => Active && LastHandledPeriodFrom != from;

    /// <inheritdoc cref="IsPending(DateOnly)"/>
    public bool IsPending(DateOnly from, DateOnly to) => IsPending(from) && HasStartedBy(from, to);

    /// <summary>Coming up soon: pending, not yet due, and its due date is within <paramref name="windowDays"/> of today.</summary>
    public bool IsUpcoming(DateOnly from, DateOnly to, DateOnly today, int windowDays)
    {
        if (!IsPending(from, to)) return false;
        var due = DueDateWithin(from, to);
        return due > today && due <= today.AddDays(windowDays);
    }

    /// <summary>Whole days until this item's due date (negative if the day has already passed).</summary>
    public int DaysUntilDue(DateOnly from, DateOnly to, DateOnly today) =>
        DueDateWithin(from, to).DayNumber - today.DayNumber;

    /// <summary>Has a predictable amount (Fixed/Typical) that can be counted toward "bills still due".</summary>
    public bool HasKnownAmount => AmountMode != RecurringAmountMode.ReminderOnly;

    private static string Clean(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A name is required.", nameof(name)) : name.Trim();
}
