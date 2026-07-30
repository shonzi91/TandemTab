namespace FinApp.Contracts;

// --- Path-B thin-client slice: Goals / Savings (docs/MOBILE.md) ----------------------------------------------
// The heaviest surface. Every figure — the accumulated total, goal progress, the debt payoff schedule, the
// investment projection, the sinking-fund set-aside — is computed server-side (the forecasting services live in the
// domain) and handed over resolved, so the thin client renders goals without running any of that math.

/// <summary>One savings bucket with all its display figures resolved. <see cref="Kind"/> is "goal"/"debt"/
/// "investment"/"sinking"; only the fields for that kind are populated:
/// <list type="bullet">
/// <item><b>goal</b>: <see cref="GoalTarget"/> + <see cref="GoalProgress"/> (0..1).</item>
/// <item><b>debt</b>: <see cref="DebtBalance"/> owed today, <see cref="DebtProgress"/> (0..1 paid off),
/// <see cref="DebtMonthsAhead"/> (whole months saved vs the contractual installment at the demonstrated pace).</item>
/// <item><b>investment</b>: <see cref="InvestmentProjected"/> future value at the current balance + pace.</item>
/// <item><b>sinking</b>: <see cref="MonthlySetAside"/> and any <see cref="TargetShortfall"/>.</item>
/// </list>
/// <see cref="Saved"/> (the accumulated earmark) and <see cref="Icon"/> (raw stored icon) apply to every kind.
/// <see cref="Forecast"/> carries the raw inputs the interactive what-if modals re-project from — null for kinds
/// with no projection (sinking). <see cref="Costs"/> lists a sinking fund's planned costs to cover (the
/// "expenses to cover" breakdown) — null/empty for every other kind.</summary>
public record SavingBucketDto(
    Guid Id,
    string Name,
    string? Icon,
    decimal Saved,
    string Kind,
    bool Archived,
    decimal? GoalTarget,
    decimal? GoalProgress,
    decimal? DebtBalance,
    decimal? DebtProgress,
    int? DebtMonthsAhead,
    decimal? InvestmentProjected,
    decimal? MonthlySetAside,
    decimal? TargetShortfall,
    SavingBucketForecastDto? Forecast = null,
    IReadOnlyList<PlannedCostDto>? Costs = null);

/// <summary>The raw knobs an interactive projection modal drags — supplied so the thin client can re-run the pure
/// forecast math (<c>FinApp.Forecasting</c>: <c>InvestmentForecast</c>/<c>LoanForecast</c>) locally with zero
/// latency, rather than round-tripping the server on every slider tick. Only the fields for the bucket's kind are
/// populated; <see cref="DemonstratedPace"/> and <see cref="PlannedContribution"/> apply to goal/debt/investment.
/// <list type="bullet">
/// <item><b>investment</b>: <see cref="InvestmentRatePercent"/>, <see cref="InvestmentTermYears"/>,
/// <see cref="InvestmentCompoundsPerYear"/> (present value is the bucket's <c>Saved</c>).</item>
/// <item><b>debt</b>: <see cref="DebtStoredBalance"/> (the anchored balance the multi-debt planner uses — the
/// <c>SavingBucketDto.DebtBalance</c> above is the balance walked forward to <i>today</i>),
/// <see cref="DebtOriginalBalance"/>, <see cref="DebtRatePercent"/>, <see cref="DebtInstallment"/>,
/// <see cref="DebtBalanceAsOf"/>.</item>
/// </list></summary>
public record SavingBucketForecastDto(
    decimal? DemonstratedPace,
    decimal? PlannedContribution,
    decimal? InvestmentRatePercent,
    decimal? InvestmentTermYears,
    int? InvestmentCompoundsPerYear,
    decimal? DebtStoredBalance,
    decimal? DebtOriginalBalance,
    decimal? DebtRatePercent,
    decimal? DebtInstallment,
    DateOnly? DebtBalanceAsOf);

/// <summary>One manual "Add to savings" deposit this period (editable/removable), the bucket name resolved.</summary>
public record SavingDepositRowDto(Guid Id, Guid BucketId, string BucketName, decimal Amount, DateOnly Date, string? Note);

/// <summary>The whole Goals surface in one read: the header figures, the amount still free to set aside
/// (<see cref="AvailableToSave"/>, the add-to-savings cap), the reallocation cap
/// (<see cref="MaxAdditionalSavings"/>, the most that can be *moved into* savings without breaking the plan — what
/// the thick "reserve it toward this loan/goal" nudges clamp to), every bucket, and this period's manual deposits.</summary>
public record SavingsViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    decimal AvailableToSave,
    decimal MaxAdditionalSavings,
    IReadOnlyList<SavingBucketDto> Buckets,
    IReadOnlyList<SavingDepositRowDto> Deposits)
{
    public static readonly SavingsViewDto Empty = new(0, "", AccountOverviewDto.Empty, 0m, 0m, [], []);
}

/// <summary>The delta a savings mutation returns: new <see cref="Version"/>, affected entity id, and the whole
/// refreshed <see cref="View"/> the client reconciles from. A superset of <see cref="MutationResultDto"/>.</summary>
public record SavingsMutationDto(long Version, Guid? EntityId, SavingsViewDto View);
