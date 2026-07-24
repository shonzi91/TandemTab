using FinApp.Domain.Periods;

namespace FinApp.Domain.Forecasting;

/// <summary>
/// The one piece of runway math that needs the domain aggregate: averaging what actually moved through the
/// account's completed periods. The pure walk-it-forward projection lives in <see cref="FinApp.Forecasting"/> (a
/// WASM-shippable leaf with no money-model dependency); this stays server-side because it reads <see cref="Period"/>.
/// </summary>
public static class CashFlowHistory
{
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
    /// earmarking doesn't move money out of the account.
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
}
