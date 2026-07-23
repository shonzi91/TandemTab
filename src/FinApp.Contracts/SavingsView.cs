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
/// <see cref="Saved"/> (the accumulated earmark) and <see cref="Icon"/> (raw stored icon) apply to every kind.</summary>
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
    decimal? TargetShortfall);

/// <summary>One manual "Add to savings" deposit this period (editable/removable), the bucket name resolved.</summary>
public record SavingDepositRowDto(Guid Id, Guid BucketId, string BucketName, decimal Amount, DateOnly Date, string? Note);

/// <summary>The whole Goals surface in one read: the header figures, the amount still free to set aside
/// (<see cref="AvailableToSave"/>, the add-to-savings cap), every bucket, and this period's manual deposits.</summary>
public record SavingsViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    decimal AvailableToSave,
    IReadOnlyList<SavingBucketDto> Buckets,
    IReadOnlyList<SavingDepositRowDto> Deposits)
{
    public static readonly SavingsViewDto Empty = new(0, "", AccountOverviewDto.Empty, 0m, [], []);
}

/// <summary>The delta a savings mutation returns: new <see cref="Version"/>, affected entity id, and the whole
/// refreshed <see cref="View"/> the client reconciles from. A superset of <see cref="MutationResultDto"/>.</summary>
public record SavingsMutationDto(long Version, Guid? EntityId, SavingsViewDto View);
