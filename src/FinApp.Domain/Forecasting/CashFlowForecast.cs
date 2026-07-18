using FinApp.Domain.Periods;

namespace FinApp.Domain.Forecasting;

/// <summary>Where the income and spending figures being projected came from — shown to the user, because a runway
/// built on two months of history means something different from one built on declared recurring items.</summary>
public enum CashFlowBasis
{
    /// <summary>Averaged from completed periods — what actually happened.</summary>
    Demonstrated = 0,
    /// <summary>Summed from declared recurring items — used when there's no history to average yet.</summary>
    Recurring = 1,
}

/// <summary>One projected month: where the balance starts, what's expected to move, and where it lands.</summary>
/// <param name="Month">First day of the month this row covers.</param>
/// <param name="Opening">Balance at the start of the month (the previous month's <paramref name="Closing"/>).</param>
/// <param name="Income">Expected money in.</param>
/// <param name="Spending">Expected money out (a positive number — it is subtracted).</param>
/// <param name="Closing">Balance at the end of the month.</param>
public readonly record struct CashFlowMonth(
    DateOnly Month, decimal Opening, decimal Income, decimal Spending, decimal Closing);

/// <summary>The projection over the requested horizon.</summary>
/// <param name="Months">One row per month, in order, starting at the requested <c>from</c> month.</param>
/// <param name="FirstShortfallMonth">The first month that ends below zero, or null if none does.</param>
/// <param name="Basis">Where <c>Income</c>/<c>Spending</c> came from.</param>
/// <param name="MonthlyCommitted">
/// Sinking-fund money earmarked per month. <b>Not subtracted from the balance</b> — earmarked money hasn't left the
/// account, it's just spoken for. Carried alongside so the UI can say "of which €X is committed" without pretending
/// the balance is lower than it is.
/// </param>
/// <param name="HasUnknownAmounts">
/// True when a recurring-basis projection skipped an item that claims no amount. Those are missing outgoings, so the
/// figure is optimistic — say so rather than presenting it as complete.
/// </param>
public sealed record CashFlowProjection(
    IReadOnlyList<CashFlowMonth> Months,
    DateOnly? FirstShortfallMonth,
    CashFlowBasis Basis,
    decimal MonthlyCommitted,
    bool HasUnknownAmounts);

/// <summary>
/// Projects the next few months of the account balance. Pure — like <see cref="LoanForecast"/> and
/// <see cref="InvestmentForecast"/> it takes numbers and returns numbers, and <b>moves no money</b>.
///
/// <para>
/// <b>It takes plain income/spending figures, not recurring items.</b> Deciding what a month costs is a question
/// about the account's history and declarations, which belongs to the caller — the first version read only declared
/// <c>RecurringItem</c>s and so reported €0 income for anyone who logs their salary as it arrives rather than
/// declaring it. Projecting from a source the user hasn't filled in produces confident nonsense.
/// </para>
///
/// <para>
/// <b>Earmarked savings are not subtracted.</b> <c>Period.ExpectedClosingBalance</c> doesn't subtract them either:
/// money set aside stays in the account, it's reserved rather than spent. Taking it off a balance projection —
/// which the first version did — double-counts against a balance that already contains it, and makes the runway
/// pessimistic. It travels as <see cref="CashFlowProjection.MonthlyCommitted"/> for display instead.
/// </para>
///
/// <para>
/// <b>Budgets are deliberately absent.</b> The app's stance (see <c>Period.FreeToAllocateAfter</c>) is that budgets
/// are advisory plans while only savings reserve cash. Counting a budget as a committed outflow would contradict the
/// "Free" figure one screen away and quietly turn this into a different budgeting method.
/// </para>
/// </summary>
public static class CashFlowForecast
{
    /// <summary>Cap the horizon — beyond a couple of years "this repeats monthly" is fiction.</summary>
    public const int MaxMonths = 24;

    /// <summary>
    /// Average money-in and money-out per period across <b>completed</b> periods, or null when there are none.
    /// This is the preferred basis for a runway: it reflects what actually happens, including the income and
    /// spending a user never declared as recurring — which is exactly the case that made the first version report
    /// €0 income and warn a healthy account it was about to run dry.
    /// <para>
    /// Only closed periods count. The period in progress is part-way through, so averaging it in drags both figures
    /// down and makes the projection look worse the earlier in the month you check it.
    /// </para>
    /// <para>
    /// Money out is expenses <i>plus</i> transfers to other accounts — both actually leave. Savings are excluded:
    /// earmarking doesn't move money out of the account (see the class docs).
    /// </para>
    /// </summary>
    public static (decimal Income, decimal Spending)? Demonstrated(IEnumerable<Period> periods)
    {
        var closed = periods.Where(p => p.Status == PeriodStatus.Closed).ToList();
        if (closed.Count == 0) return null;

        var income = closed.Sum(p => p.ContributionsPaidTotal.Amount) / closed.Count;
        var spending = closed.Sum(p => p.ExpensesTotal.Amount + p.ExternalOutTotal.Amount) / closed.Count;
        return (decimal.Round(income, 2), decimal.Round(spending, 2));
    }

    /// <summary>
    /// Walk <paramref name="months"/> months forward from <paramref name="openingBalance"/>, applying a flat
    /// monthly <paramref name="income"/> and <paramref name="spending"/>.
    /// </summary>
    /// <param name="openingBalance">Cash at the start of the first projected month — pass the same figure the UI shows.</param>
    /// <param name="income">Expected money in per month (negatives are floored to zero).</param>
    /// <param name="spending">Expected money out per month (negatives are floored to zero).</param>
    /// <param name="from">Any date in the first month to project; the row is dated to that month's 1st.</param>
    /// <param name="months">How many months to project (clamped to 1..<see cref="MaxMonths"/>).</param>
    /// <param name="basis">Where the figures came from, for display.</param>
    /// <param name="monthlyCommitted">Sinking-fund earmark per month — reported, not subtracted.</param>
    /// <param name="hasUnknownAmounts">Whether some outgoings couldn't be quantified.</param>
    public static CashFlowProjection Project(
        decimal openingBalance,
        decimal income,
        decimal spending,
        DateOnly from,
        int months,
        CashFlowBasis basis = CashFlowBasis.Demonstrated,
        decimal monthlyCommitted = 0m,
        bool hasUnknownAmounts = false)
    {
        var horizon = Math.Clamp(months, 1, MaxMonths);
        var inPerMonth = Math.Max(0m, income);
        var outPerMonth = Math.Max(0m, spending);

        var rows = new List<CashFlowMonth>(horizon);
        var start = new DateOnly(from.Year, from.Month, 1);
        var balance = openingBalance;
        DateOnly? firstShortfall = null;

        for (var i = 0; i < horizon; i++)
        {
            var month = start.AddMonths(i);
            var opening = balance;
            var closing = decimal.Round(opening + inPerMonth - outPerMonth, 2);

            rows.Add(new CashFlowMonth(month, opening, inPerMonth, outPerMonth, closing));
            if (firstShortfall is null && closing < 0m) firstShortfall = month;
            balance = closing;
        }

        return new CashFlowProjection(rows, firstShortfall, basis, Math.Max(0m, monthlyCommitted), hasUnknownAmounts);
    }
}
