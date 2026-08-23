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

    /// <summary>
    /// The whole-stack payoff plan (<see cref="DebtPlanDto"/>): one spare amount across every debt, the clearing
    /// order under avalanche or snowball, the debt-free date and the total interest — plus the separate
    /// "where am I actually heading" forecast at the pace the user has demonstrated.
    ///
    /// <para>★ Its own read rather than a field on <see cref="View"/>, for the same reason the per-bucket payoff
    /// is: it runs an amortisation per debt per call, and every Goals render would otherwise pay for a plan
    /// nobody has opened. Unlike the per-bucket one it takes the caller's <paramref name="extraPerMonth"/> and
    /// <paramref name="strategy"/>, because those are the two things the screen exists to let you change.</para>
    ///
    /// <para>⚠️ <b>Not Pro-gated, deliberately.</b> The web's planner card is free — only the per-bucket modelling
    /// of the bank's alternatives is withheld — and a 402 here would withhold the debt-free date the Home card has
    /// always shown for nothing.</para>
    /// </summary>
    /// <param name="extraPerMonth">The spare amount thrown at the stack each month, on top of every installment.</param>
    /// <param name="strategy">"avalanche" (default, highest rate first) or "snowball" (smallest balance first).</param>
    public static DebtPlanDto Plan(Account account, decimal extraPerMonth = 0m, string? strategy = null, Period? viewPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var period = viewPeriod ?? account.CurrentPeriod;
        var mode = string.Equals(strategy, "snowball", StringComparison.OrdinalIgnoreCase)
            ? LoanForecast.Strategy.Snowball
            : LoanForecast.Strategy.Avalanche;
        var modeName = mode == LoanForecast.Strategy.Snowball ? "snowball" : "avalanche";
        var extra = Math.Max(0m, extraPerMonth);

        // Mirrors BudgetingState.DebtLoanInputs exactly: live debt only. An archived bucket or a cleared one is
        // not part of "when am I debt-free", and including either would push the date out for a debt nobody owes.
        var debts = account.SavingCategories
            .Where(b => b is { IsDebt: true, IsArchived: false } && b.DebtBalance > 0m)
            .ToList();

        var basics = DebtPlanDto.None with
        {
            Currency = account.Currency,
            DebtCount = debts.Count,
            Strategy = modeName,
            ExtraPerMonth = extra,
            TotalOwed = decimal.Round(debts.Sum(b => b.DebtBalance), 2),
            TotalInstallments = decimal.Round(debts.Sum(b => b.DebtInstallment), 2),
        };
        if (debts.Count == 0) return basics;

        // The date the plan is counted from — the period being looked at, not "now", so this agrees with the
        // per-bucket payoff read beside it rather than drifting by however long ago that period started.
        var anchor = period?.From ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var report = new SavingsReportService();

        // ── The forecast half: each debt at its installment + the pace actually demonstrated. This is the Home
        // "Debt-free" line, and it is a different question from the plan — see the contract.
        var paceMonths = 0;
        var paceInterestSaved = 0m;
        var paceClears = true;
        foreach (var d in debts)
        {
            var pace = report.AverageDepositPace(account, d.Id)?.Amount ?? 0m;
            if (LoanForecast.PayOff(d.DebtBalance, d.DebtAnnualRatePercent, d.DebtInstallment + pace) is not { } p)
            {
                paceClears = false;   // one debt never clears at that amount → no honest date to promise
                break;
            }
            paceMonths = Math.Max(paceMonths, p.Months);   // you are debt-free when the LAST one clears
            if (pace > 0m &&
                LoanForecast.SimulateExtra(d.DebtBalance, d.DebtAnnualRatePercent, d.DebtInstallment, pace) is { } sim)
                paceInterestSaved += sim.InterestSaved;
        }
        basics = basics with
        {
            PaceMonths = paceClears ? paceMonths : null,
            PaceDebtFreeOn = paceClears ? anchor.AddMonths(paceMonths) : null,
            PaceInterestSaved = paceClears ? decimal.Round(paceInterestSaved, 2) : 0m,
        };

        // ── The plan half: the strategy, at the extra the caller is considering.
        var inputs = debts
            .Select(b => new LoanForecast.LoanInput(b.Id, b.Name, b.DebtBalance, b.DebtAnnualRatePercent, b.DebtInstallment))
            .ToList();
        // ⚠️ Null means the stack never clears at these installments — the minimums don't cover the interest.
        // That is an honest "we can't say", and the client's job is to ask for an extra amount, not to print a
        // date. Same rule the per-bucket schedule takes when PayOff returns null.
        if (LoanForecast.PlanPayoff(inputs, extra, mode) is not { } plan) return basics;

        // The baseline is the SAME strategy with no extra — the only comparison that isolates what the extra buys.
        var baseline = extra > 0m ? LoanForecast.PlanPayoff(inputs, 0m, mode) : plan;

        return basics with
        {
            Available = true,
            Months = plan.Months,
            DebtFreeOn = anchor.AddMonths(plan.Months),
            TotalInterest = plan.TotalInterest,
            MonthsSaved = baseline is { } b0 ? Math.Max(0, b0.Months - plan.Months) : 0,
            InterestSaved = baseline is { } b1 ? decimal.Round(Math.Max(0m, b1.TotalInterest - plan.TotalInterest), 2) : 0m,
            Order = plan.Order.Select(o =>
            {
                var bucket = account.FindSavingCategory(o.Id);
                return new PlanLoanDto(o.Id, o.Name, bucket?.Icon,
                    decimal.Round(bucket?.DebtBalance ?? 0m, 2),
                    bucket?.DebtAnnualRatePercent ?? 0m,
                    decimal.Round(bucket?.DebtInstallment ?? 0m, 2),
                    o.ClearedInMonth,
                    anchor.AddMonths(o.ClearedInMonth));
            }).ToList(),
        };
    }
}
