using FinApp.Domain.Accounts;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Forecasting;

namespace FinApp.Domain.Services;

/// <summary>
/// Account-level forecasting composed from the domain aggregate — moved server-side under the Option-A
/// migration (docs/MOBILE.md). Mirrors <c>BudgetingState.ProjectCashFlow</c> exactly, so the runway can't
/// drift between web and (future) native. Pure — moves no money.
/// </summary>
/// <summary>Whether a target is the all-debts debt-free date or a single savings goal.</summary>
public enum TargetKind { DebtFree, Goal }

/// <summary>A single Home "on track for" target. For a goal, <see cref="Name"/> is the bucket name and
/// <see cref="Icon"/> its stored icon; for debt-free both are empty/null (the client supplies the label + flag icon).
/// A reached goal has <see cref="Reached"/> true and <see cref="Months"/> 0.</summary>
public readonly record struct AccountTarget(TargetKind Kind, string Name, string? Icon, int Months, bool Reached);

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

        if (CashFlowHistory.Demonstrated(account.Periods) is { } seen)
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

    /// <summary>
    /// The Home "on track for" targets — moved server-side under the Option-A migration (docs/MOBILE.md). Mirrors
    /// <c>Dashboard.HomeTargets</c> exactly so web and (future) native agree: the all-debts debt-free date (each debt
    /// paid at its installment plus the pace actually set aside; the latest clearing month wins), plus every ordinary
    /// savings goal dated at its demonstrated pace. Empty when there's nothing to project (no current period, no debts,
    /// no funded goals) — the caller hides the card. A reached goal is <c>Reached</c> with <c>Months</c> 0.
    /// </summary>
    public static IReadOnlyList<AccountTarget> Targets(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.CurrentPeriod is not { } period) return [];

        var savings = new SavingsReportService();
        var list = new List<AccountTarget>();

        if (DebtFreeMonths(account, savings) is { } dfMonths)
            list.Add(new AccountTarget(TargetKind.DebtFree, "", null, dfMonths, false));

        foreach (var bucket in account.SavingCategories)
        {
            if (bucket.IsDebt || bucket.IsArchived || !bucket.HasGoal || bucket.GoalAmount is not { } goal) continue;
            var saved = savings.ForBucket(account, period, bucket.Id).AccumulatedTotal.Amount;
            if (saved >= goal) { list.Add(new AccountTarget(TargetKind.Goal, bucket.Name, bucket.Icon, 0, true)); continue; }
            if (savings.AverageDepositPace(account, bucket.Id) is { Amount: > 0m } pace)
                list.Add(new AccountTarget(TargetKind.Goal, bucket.Name, bucket.Icon,
                    (int)Math.Ceiling((double)((goal - saved) / pace.Amount)), false));
        }
        return list;
    }

    /// <summary>Whole months until every debt bucket is cleared, each paid at its installment plus the demonstrated
    /// pace — the latest clearing month wins (mirrors <c>Dashboard.DebtFreeMonthsAtPace</c>). Null when there are no
    /// debts, or one never clears at that amount (no honest date to promise).</summary>
    private static int? DebtFreeMonths(Account account, SavingsReportService savings)
    {
        var debts = account.SavingCategories.Where(b => b.IsDebt && !b.IsArchived && b.DebtBalance > 0m).ToList();
        if (debts.Count == 0) return null;
        var months = 0;
        foreach (var d in debts)
        {
            var pace = savings.AverageDepositPace(account, d.Id)?.Amount ?? 0m;
            if (LoanForecast.PayOff(d.DebtBalance, d.DebtAnnualRatePercent, d.DebtInstallment + pace) is not { } p)
                return null;
            months = Math.Max(months, p.Months);
        }
        return months;
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
