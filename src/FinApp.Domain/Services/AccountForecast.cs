using FinApp.Domain.Accounts;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;

namespace FinApp.Domain.Services;

/// <summary>
/// Account-level forecasting composed from the domain aggregate — moved server-side under the Option-A
/// migration (docs/MOBILE.md). Mirrors <c>BudgetingState.ProjectCashFlow</c> exactly, so the runway can't
/// drift between web and (future) native. Pure — moves no money.
/// </summary>
public static class AccountForecast
{
    /// <summary>
    /// The cash runway for the account's current period: where the balance lands over the next
    /// <paramref name="months"/> months. <b>Null</b> when there's no trustworthy basis — no completed period to
    /// average and nothing recurring declared — because a projection with no income signal reports certain ruin
    /// for anyone who simply hasn't filled that in yet.
    /// <para><b>Demonstrated history wins over declarations</b> (completed-period averages reflect what actually
    /// happens, incl. income never declared as recurring); recurring items are the fallback for a young account.</para>
    /// <para>Opening balance is the current period's expected closing balance. The header's live-bank adjustment is
    /// a client display concern and is not applied here — identical on accounts with no synced fund.</para>
    /// </summary>
    public static CashFlowProjection? Runway(Account account, int months = 6)
    {
        if (account.CurrentPeriod is not { } period) return null;

        var committed = TotalMonthlySetAside(account, period);
        var opening = period.ExpectedClosingBalance.Amount;

        if (CashFlowForecast.Demonstrated(account.Periods) is { } seen)
            return CashFlowForecast.Project(
                opening, seen.Income, seen.Spending, period.From, months,
                CashFlowBasis.Demonstrated, committed);

        var active = account.RecurringItems.Where(r => r.Active).ToList();
        var counted = active.Where(r => r.HasKnownAmount).ToList();
        if (counted.Count == 0) return null;   // nothing to average and nothing declared — say nothing

        return CashFlowForecast.Project(
            opening,
            counted.Where(r => r.Kind == RecurringKind.Income).Sum(r => r.ExpectedAmount),
            counted.Where(r => r.Kind == RecurringKind.Expense).Sum(r => r.ExpectedAmount),
            period.From, months, CashFlowBasis.Recurring, committed,
            hasUnknownAmounts: active.Any(r => !r.HasKnownAmount));
    }

    /// <summary>What every live sinking fund jointly claims each month — the same sum as
    /// <c>BudgetingState.TotalMonthlySetAside</c> (archived buckets excluded; each bucket's set-aside discounts what
    /// it already holds).</summary>
    private static decimal TotalMonthlySetAside(Account account, Period period)
    {
        var savings = new SavingsReportService();
        decimal total = 0m;
        foreach (var bucket in account.SavingCategories)
        {
            if (bucket.IsArchived || !bucket.HasCosts) continue;
            var saved = savings.ForBucket(account, period, bucket.Id).AccumulatedTotal.Amount;
            total += bucket.MonthlySetAside(period.From, saved);
        }
        return total;
    }
}
