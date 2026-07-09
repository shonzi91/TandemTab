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

    /// <summary>Day of the month it's expected (1–28, so it exists in every month).</summary>
    public int DayOfMonth { get; private set; }

    /// <summary>Expense category (for a bill) or contribution category (for income).</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>The fund the money moves out of / into.</summary>
    public Guid FundId { get; private set; }

    public bool Active { get; private set; } = true;

    /// <summary>The <c>From</c> date of the period this item was last handled (posted or skipped) — so it only goes
    /// "due" once per period. Null until first handled.</summary>
    public DateOnly? LastHandledPeriodFrom { get; private set; }

    public RecurringItem(string name, RecurringKind kind, RecurringAmountMode amountMode, decimal expectedAmount,
        int dayOfMonth, Guid categoryId, Guid fundId, string? icon = null)
    {
        Name = Clean(name);
        Kind = kind;
        AmountMode = amountMode;
        ExpectedAmount = amountMode == RecurringAmountMode.ReminderOnly ? 0m : Math.Max(0m, expectedAmount);
        DayOfMonth = Math.Clamp(dayOfMonth, 1, 28);
        CategoryId = categoryId;
        FundId = fundId;
        Icon = icon;
    }

    public void Update(string name, RecurringAmountMode amountMode, decimal expectedAmount, int dayOfMonth,
        Guid categoryId, Guid fundId, string? icon)
    {
        Name = Clean(name);
        AmountMode = amountMode;
        ExpectedAmount = amountMode == RecurringAmountMode.ReminderOnly ? 0m : Math.Max(0m, expectedAmount);
        DayOfMonth = Math.Clamp(dayOfMonth, 1, 28);
        CategoryId = categoryId;
        FundId = fundId;
        Icon = icon;
    }

    public void SetActive(bool active) => Active = active;

    /// <summary>Mark handled for the period starting <paramref name="periodFrom"/> (posted or skipped), so it isn't
    /// "due" again until the next period.</summary>
    public void MarkHandled(DateOnly periodFrom) => LastHandledPeriodFrom = periodFrom;

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

    /// <summary>Due (and not yet handled) within period [from, to] as of <paramref name="today"/> — i.e. its day has
    /// arrived and it hasn't been posted or skipped this period.</summary>
    public bool IsDue(DateOnly from, DateOnly to, DateOnly today) =>
        Active && LastHandledPeriodFrom != from && today >= DueDateWithin(from, to);

    /// <summary>Active and not yet handled this period (whether or not its day has arrived) — i.e. still expected.</summary>
    public bool IsPending(DateOnly from) => Active && LastHandledPeriodFrom != from;

    /// <summary>Coming up soon: pending, not yet due, and its due date is within <paramref name="windowDays"/> of today.</summary>
    public bool IsUpcoming(DateOnly from, DateOnly to, DateOnly today, int windowDays)
    {
        if (!IsPending(from)) return false;
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
