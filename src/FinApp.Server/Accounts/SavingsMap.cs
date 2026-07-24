using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Forecasting;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the Path-B thin-Goals read model (<see cref="SavingsViewDto"/>) from the domain aggregate. This is the
/// surface that most justifies Option A: goal progress, the debt payoff schedule and the investment projection are all
/// computed here — mirroring <c>BudgetingState</c>'s savings reads exactly — so the thin client renders none of it.
/// </summary>
public static class SavingsMap
{
    public static SavingsViewDto View(Account account, long version, decimal? bankBalance = null, string? bankCurrency = null, Period? viewPeriod = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return SavingsViewDto.Empty with { Version = version, Currency = account.Currency };

        var report = new SavingsReportService();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var priorSaved = report.AccumulatedTotal(account) - period.SavingsNetTotal;
        var availableToSave = period.AvailableToSaveAfter(priorSaved).Amount;
        var maxAdditionalSavings = period.MaxAdditionalSavingsAfter(priorSaved).Amount;

        var buckets = account.SavingCategories
            .Select(b => Bucket(account, period, report, b, today))
            .ToList();

        var deposits = period.ManualSavingDeposits()
            .OrderByDescending(a => a.Date)
            .Select(a => new SavingDepositRowDto(a.Id, a.SavingCategoryId,
                account.FindSavingCategory(a.SavingCategoryId)?.Name ?? "—", a.Amount.Amount, a.Date, a.Note))
            .ToList();

        return new SavingsViewDto(version, account.Currency, SpendingMap.Overview(account, period, bankBalance, bankCurrency),
            availableToSave, maxAdditionalSavings, buckets, deposits);
    }

    private static SavingBucketDto Bucket(Account account, Period period, SavingsReportService report, SavingCategory b, DateOnly today)
    {
        var saved = report.ForBucket(account, period, b.Id).AccumulatedTotal.Amount;
        var kind = b.IsDebt ? "debt" : b.IsInvestment ? "investment" : b.IsExpensesFund ? "sinking" : "goal";

        decimal? goalTarget = null, goalProgress = null, debtBalance = null, debtProgress = null,
                 investmentProjected = null, monthlySetAside = null, targetShortfall = null;
        int? debtMonthsAhead = null;
        SavingBucketForecastDto? forecast = null;
        var pace = report.AverageDepositPace(account, b.Id)?.Amount;

        switch (kind)
        {
            case "goal":
                goalTarget = b.GoalAmount;
                goalProgress = report.GoalProgress(account, b.Id).Ratio;
                forecast = new SavingBucketForecastDto(pace, b.PlannedContribution,
                    null, null, null, null, null, null, null, null);
                break;
            case "debt":
                debtBalance = b.DebtBalanceOn(today);
                debtProgress = b.DebtProgressRatioOn(today);
                debtMonthsAhead = DebtMonthsAhead(account, report, b);
                forecast = new SavingBucketForecastDto(pace, b.PlannedContribution,
                    null, null, null,
                    b.DebtBalance, b.DebtOriginalBalance, b.DebtAnnualRatePercent, b.DebtInstallment, b.DebtBalanceAsOf);
                break;
            case "investment":
                investmentProjected = InvestmentForecast
                    .Project(saved, b.InvestmentAnnualRatePercent, b.InvestmentTermYears, b.InvestmentCompoundsPerYear, 0m)
                    .FutureValue;
                forecast = new SavingBucketForecastDto(pace, b.PlannedContribution,
                    b.InvestmentAnnualRatePercent, b.InvestmentTermYears, b.InvestmentCompoundsPerYear,
                    null, null, null, null, null);
                break;
            case "sinking":
                var setAside = b.MonthlySetAside(period.From, saved);
                monthlySetAside = setAside > 0m ? setAside : null;
                var shortfall = b.TargetShortfall(saved);
                targetShortfall = shortfall > 0m ? shortfall : null;
                break;
        }

        return new SavingBucketDto(b.Id, b.Name, b.Icon, saved, kind, b.IsArchived,
            goalTarget, goalProgress, debtBalance, debtProgress, debtMonthsAhead,
            investmentProjected, monthlySetAside, targetShortfall, forecast);
    }

    // Whole months a debt is paid off ahead of its contractual installment, at the demonstrated pace. Mirrors
    // BudgetingState.SavingBucketMonthsAhead. Null when there's no meaningful speed-up.
    private static int? DebtMonthsAhead(Account account, SavingsReportService report, SavingCategory b)
    {
        if (!b.IsDebt || b.DebtOriginalBalance <= 0m) return null;
        var pace = report.AverageDepositPace(account, b.Id)?.Amount ?? 0m;
        var extra = pace - b.DebtInstallment;
        if (extra <= 0m) return null;
        var sim = LoanForecast.SimulateExtra(b.DebtOriginalBalance, b.DebtAnnualRatePercent, b.DebtInstallment, extra);
        return sim is { MonthsSaved: > 0 } s ? s.MonthsSaved : null;
    }
}
