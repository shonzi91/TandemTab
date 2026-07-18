using FinApp.Domain.Recurring;

namespace FinApp.Domain.Forecasting;

/// <summary>One projected month: where the balance starts, what's expected to move, and where it lands.</summary>
/// <param name="Month">First day of the month this row covers.</param>
/// <param name="Opening">Balance at the start of the month (the previous month's <paramref name="Closing"/>).</param>
/// <param name="Income">Expected recurring income landing this month.</param>
/// <param name="Bills">Expected recurring bills leaving this month (a positive number — it is subtracted).</param>
/// <param name="SetAside">Sinking-fund money committed this month (positive — also subtracted; see the class docs).</param>
/// <param name="Closing">Balance at the end of the month.</param>
public readonly record struct CashFlowMonth(
    DateOnly Month, decimal Opening, decimal Income, decimal Bills, decimal SetAside, decimal Closing);

/// <summary>The projection over the requested horizon.</summary>
/// <param name="Months">One row per month, in order, starting at the requested <c>from</c> month.</param>
/// <param name="FirstShortfallMonth">The first month that ends below zero, or null if none does.</param>
/// <param name="HasUnknownAmounts">
/// True when at least one active recurring item claims no amount (<see cref="RecurringAmountMode.ReminderOnly"/>).
/// Those are skipped, so the projection is <b>optimistic</b> — it is missing real outgoings. Surface this rather than
/// presenting an incomplete figure as a confident one.
/// </param>
public sealed record CashFlowProjection(
    IReadOnlyList<CashFlowMonth> Months, DateOnly? FirstShortfallMonth, bool HasUnknownAmounts);

/// <summary>
/// Projects the next few months of cash from what's already known to repeat: recurring income, recurring bills, and
/// the flat monthly set-aside any sinking funds need. Pure — like <see cref="LoanForecast"/> and
/// <see cref="InvestmentForecast"/> it takes numbers and returns numbers, and <b>moves no money</b>.
///
/// <para>
/// <b>Budgets are deliberately not in it.</b> The app's stance (see <c>Period.FreeToAllocateAfter</c>) is that
/// budgets are advisory plans while only savings reserve cash. Treating a budget as a committed outflow here would
/// contradict the "Free" figure one screen away and quietly turn the app into a different budgeting method. What
/// this answers is narrower and honest: <i>given only what I've told it repeats, when does the money run out?</i>
/// </para>
///
/// <para>
/// <b>Set-aside is treated as a real claim on cash</b>, because in this model it is one — money moved into a savings
/// bucket is reserved, not spendable. It enters as a smoothed monthly figure rather than the lumpy bill it covers,
/// which is the whole point of a sinking fund. ⚠️ That means a cost listed as a <c>PlannedCost</c> <i>and</i> as a
/// <see cref="RecurringItem"/> would be counted twice — they are separate lists and nothing reconciles them.
/// </para>
///
/// <para>
/// <b>Monthly by construction.</b> <see cref="RecurringItem"/> repeats monthly on a day 1–28, so every active item
/// with a known amount lands exactly once in every projected month; there is no schedule to walk. Day-of-month is
/// therefore irrelevant to a month-granularity projection and is ignored.
/// </para>
/// </summary>
public static class CashFlowForecast
{
    /// <summary>Cap the horizon — beyond a year or so a projection built from "this repeats monthly" is fiction.</summary>
    public const int MaxMonths = 24;

    /// <summary>
    /// Walk <paramref name="months"/> months forward from <paramref name="openingBalance"/>, applying recurring
    /// income and bills plus a flat <paramref name="monthlySetAside"/>.
    /// </summary>
    /// <param name="openingBalance">Cash at the start of the first projected month.</param>
    /// <param name="recurring">All recurring items; inactive and amount-less ones are filtered out here.</param>
    /// <param name="monthlySetAside">Total sinking-fund commitment per month across all buckets (0 if none).</param>
    /// <param name="from">Any date in the first month to project; the row is dated to that month's 1st.</param>
    /// <param name="months">How many months to project (clamped to 1..<see cref="MaxMonths"/>).</param>
    public static CashFlowProjection Project(
        decimal openingBalance,
        IEnumerable<RecurringItem> recurring,
        decimal monthlySetAside,
        DateOnly from,
        int months)
    {
        var horizon = Math.Clamp(months, 1, MaxMonths);
        var all = recurring as IReadOnlyList<RecurringItem> ?? recurring.ToList();

        var active = all.Where(r => r.Active).ToList();
        // ReminderOnly claims no amount. Skipping it is the only honest option — but it makes the projection
        // optimistic, so the caller is told rather than left to assume the figure is complete.
        var hasUnknown = active.Any(r => !r.HasKnownAmount);
        var counted = active.Where(r => r.HasKnownAmount).ToList();

        var income = counted.Where(r => r.Kind == RecurringKind.Income).Sum(r => r.ExpectedAmount);
        var bills = counted.Where(r => r.Kind == RecurringKind.Expense).Sum(r => r.ExpectedAmount);
        var setAside = Math.Max(0m, monthlySetAside);

        var rows = new List<CashFlowMonth>(horizon);
        var start = new DateOnly(from.Year, from.Month, 1);
        var balance = openingBalance;
        DateOnly? firstShortfall = null;

        for (var i = 0; i < horizon; i++)
        {
            var month = start.AddMonths(i);
            var opening = balance;
            var closing = decimal.Round(opening + income - bills - setAside, 2);

            rows.Add(new CashFlowMonth(month, opening, income, bills, setAside, closing));
            if (firstShortfall is null && closing < 0m) firstShortfall = month;
            balance = closing;
        }

        return new CashFlowProjection(rows, firstShortfall, hasUnknown);
    }
}
