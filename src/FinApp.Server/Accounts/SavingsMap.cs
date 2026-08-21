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
            availableToSave, maxAdditionalSavings, buckets, deposits, Movements(account, period));
    }

    /// <summary>
    /// This period's movements of already-saved money — everything in the allocation ledger that is not a plain
    /// deposit, which is exactly the set the deposits list leaves out.
    /// </summary>
    private static List<SavingMovementRowDto> Movements(Account account, Period period)
    {
        string BucketName(Guid id) => account.FindSavingCategory(id)?.Name ?? "—";
        var depositIds = period.ManualSavingDeposits().Select(a => a.Id).ToHashSet();

        return period.SavingAllocations
            .Where(a => !depositIds.Contains(a.Id) && !a.Amount.IsZero)
            .OrderByDescending(a => a.Date)
            .Select(a =>
            {
                var outgoing = a.Amount.IsNegative;
                var (kind, counterpart) = a switch
                {
                    // A budget move names the category it matured into.
                    { BudgetCategoryId: { } cat } => ("to-budget", account.FindCategory(cat)?.Name),
                    // A transfer names the bucket on the other end, found through the pair id it shares.
                    { TransferPairId: { } pair } => (
                        outgoing ? "transfer-out" : "transfer-in",
                        period.SavingAllocations
                            .Where(o => o.TransferPairId == pair && o.Id != a.Id)
                            .Select(o => BucketName(o.SavingCategoryId))
                            .FirstOrDefault()),
                    // Deploying the bucket to its purpose: paired with an external transfer out.
                    { SourceExternalTransferId: not null } => ("disbursed", (string?)null),
                    // Savings spent through the expense ledger — it names the expense's category.
                    { SourceExpenseId: { } exp } => (
                        "spent",
                        period.Expenses.FirstOrDefault(e => e.Id == exp) is { } e
                            ? account.FindCategory(e.CategoryId)?.Name
                            : null),
                    _ => ("disbursed", (string?)null),
                };
                return new SavingMovementRowDto(
                    a.Id, a.SavingCategoryId, BucketName(a.SavingCategoryId), kind,
                    Math.Abs(a.Amount.Amount), a.Date, a.Note, counterpart,
                    // ⚠️ Only what RemoveSavingMovement actually accepts. It reverses a budget move, a transfer pair
                    // and a disbursement; a drawdown linked to an EXPENSE falls through to its throw, because that
                    // one is undone by deleting the expense that caused it. A "spent" row offering an undo would be
                    // a control whose only outcome is a 400. The incoming half of a transfer is excluded for a
                    // different reason — it would work, but it is the same single reversal wearing a second button.
                    Undoable: kind is "to-budget" or "transfer-out" or "disbursed");
            })
            .ToList();
    }

    private static SavingBucketDto Bucket(Account account, Period period, SavingsReportService report, SavingCategory b, DateOnly today)
    {
        var saved = report.ForBucket(account, period, b.Id).AccumulatedTotal.Amount;
        var kind = b.IsDebt ? "debt" : b.IsInvestment ? "investment" : b.IsExpensesFund ? "sinking" : "goal";

        decimal? goalTarget = null, goalProgress = null, debtBalance = null, debtProgress = null,
                 investmentProjected = null, monthlySetAside = null, targetShortfall = null,
                 debtPaidInterest = null, debtRemainingInterest = null;
        int? debtMonthsAhead = null, debtInstallmentDay = null;
        var debtPaidInterestEstimated = false;
        SavingBucketForecastDto? forecast = null;
        IReadOnlyList<PlannedCostDto>? costs = null;
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
                // R1 read-outs: interest paid to date + interest still to pay from today (null when there's no
                // schedule to compute them from), the due-day, and whether paid-interest is an estimate.
                if (b.DebtInstallment > 0m)
                {
                    debtPaidInterest = b.PaidInterestSoFar(today);
                    debtRemainingInterest = b.RemainingInterest(today);
                }
                debtInstallmentDay = b.DebtInstallmentDay;
                debtPaidInterestEstimated = b.DebtPaidInterestIsEstimate;
                forecast = new SavingBucketForecastDto(pace, b.PlannedContribution,
                    null, null, null,
                    b.DebtBalance, b.DebtOriginalBalance, b.DebtAnnualRatePercent, b.DebtInstallment, b.DebtBalanceAsOf,
                    b.DebtStartDate);
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
                // The "expenses to cover" breakdown — soonest-due targets first, then the recurring costs. Mirrors
                // how the web renders the sinking-fund cost list under the expanded bucket.
                costs = b.Costs
                    .OrderBy(c => c.IsTarget ? 0 : 1)
                    .ThenBy(c => c.DueDate ?? DateOnly.MaxValue)
                    .Select(c => new PlannedCostDto(c.Label, c.Amount, CadenceString(c.Cadence), c.DueDate))
                    .ToList();
                break;
        }

        return new SavingBucketDto(b.Id, b.Name, b.Icon, saved, kind, b.IsArchived,
            goalTarget, goalProgress, debtBalance, debtProgress, debtMonthsAhead,
            investmentProjected, monthlySetAside, targetShortfall, forecast, costs,
            debtPaidInterest, debtRemainingInterest, debtInstallmentDay, debtPaidInterestEstimated,
            b.DebtPaymentDriven,
            // Edit-form prefill: the upsert overwrites all three, so a client that can't read them back wipes them.
            b.FundId, b.AlertThreshold * 100m, b.NotifyOnMilestone, b.InitialAmount,
            // The derived goal travels with the figure it was derived from, so the client can show the basis
            // ("3 × €850 a month") instead of an unexplained target the user never typed.
            b.IsEmergencyFund,
            b.IsEmergencyFund ? account.EssentialSpendPerPeriod() : null,
            b.DebtResidual);
    }

    // Domain cadence → the canonical wire string the write side (SavingBucketConfig.Cadence) round-trips.
    private static string CadenceString(CostCadence cadence) => cadence switch
    {
        CostCadence.Monthly => "monthly",
        CostCadence.Quarterly => "quarterly",
        CostCadence.Yearly => "yearly",
        _ => "one-off",
    };

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

    /// <summary>
    /// The debt-payoff read (<see cref="DebtPayoffDto"/>): the schedule, the one-off lump, the two offers a bank
    /// might make, and a small curve for the "extra per month" slider.
    /// <para>
    /// ★ Every figure comes from <see cref="LoanForecast"/>, here, for the reason this whole class exists: the
    /// thin client must render the forecast rather than compute it. A payoff date is exactly the kind of number
    /// that looks plausible when it is wrong.
    /// </para>
    /// </summary>
    /// <param name="proDebt">Whether the caller's plan includes the debt feature. Free gets the schedule and the
    /// lump-sum figures; the modelling of the bank's alternatives is the withheld part, mirroring the web.</param>
    public static DebtPayoffDto Payoff(Account account, Guid bucketId, bool proDebt, Period? viewPeriod = null)
    {
        if (account.FindSavingCategory(bucketId) is not { IsDebt: true } b) return DebtPayoffDto.None;
        var period = viewPeriod ?? account.CurrentPeriod;
        var balance = b.DebtBalance;
        var basics = DebtPayoffDto.None with
        {
            Currency = account.Currency,
            Balance = balance,
            Installment = b.DebtInstallment,
            AnnualRatePercent = b.DebtAnnualRatePercent,
        };

        // ⚠️ No schedule to walk: a payment-driven loan states no installment, and an installment that cannot
        // out-run the monthly interest never clears. Both are honest "we can't say" answers, and PayOff returns
        // null for each — better than a date nobody will see.
        if (LoanForecast.PayOff(balance, b.DebtAnnualRatePercent, b.DebtInstallment, b.DebtResidual) is not { } payoff)
            return basics;

        var report = new SavingsReportService();
        var setAside = period is null ? 0m : report.ForBucket(account, period, b.Id).AccumulatedTotal.Amount;
        var lump = setAside > 0m
            ? LoanForecast.PayLumpSum(balance, b.DebtAnnualRatePercent, b.DebtInstallment, setAside)
            : null;

        var offers = new List<PayoffOfferDto>();
        var curve = new List<PayoffCurvePointDto>();
        if (proDebt && lump is { } l)
        {
            var after = Math.Max(0m, balance - setAside);
            // Shorter term: keep paying the same installment, finish sooner.
            offers.Add(new PayoffOfferDto("shorter", b.DebtInstallment, l.AfterKeepingInstallment.Months,
                l.AfterKeepingInstallment.TotalInterest,
                decimal.Round(payoff.TotalInterest - l.AfterKeepingInstallment.TotalInterest, 2)));
            // Lower installment: keep the original end date, pay less each month. Null when the remaining term is
            // zero — there is no "same term" left to spread anything over.
            if (LoanForecast.PaymentFor(after, b.DebtAnnualRatePercent, payoff.Months) is { } lower && payoff.Months > 0)
            {
                var lowerInterest = decimal.Round(lower * payoff.Months - after, 2);
                offers.Add(new PayoffOfferDto("lower", lower, payoff.Months, lowerInterest,
                    decimal.Round(payoff.TotalInterest - lowerInterest, 2)));
            }
        }
        if (proDebt)
        {
            // A fixed ladder of steps rather than a continuous function — see PayoffCurvePointDto for why the
            // slider snaps to server-computed points. Steps scale with the installment so the ladder means the
            // same thing on a €120 phone contract and a €900 mortgage.
            var step = decimal.Round(Math.Max(5m, b.DebtInstallment / 10m), 0);
            for (var i = 1; i <= 10; i++)
            {
                var extra = step * i;
                if (LoanForecast.SimulateExtra(balance, b.DebtAnnualRatePercent, b.DebtInstallment, extra) is { } sim)
                    curve.Add(new PayoffCurvePointDto(extra, sim.MonthsSaved, sim.InterestSaved));
            }
        }

        return basics with
        {
            Available = true,
            Months = payoff.Months,
            // The date the schedule runs out, counted from the period we are looking at rather than from "now" —
            // the rest of this read is period-scoped and a payoff date that ignored that would disagree with it.
            PayoffOn = (period?.From ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddMonths(payoff.Months),
            TotalInterest = payoff.TotalInterest,
            SetAside = setAside,
            LumpBalanceAfter = Math.Max(0m, balance - setAside),
            LumpMonthsSaved = lump?.MonthsSaved ?? 0,
            LumpInterestSaved = lump?.InterestSaved ?? 0m,
            LumpClearsTheLoan = lump?.ClearsTheLoan ?? false,
            Offers = offers,
            Curve = curve,
        };
    }
}
