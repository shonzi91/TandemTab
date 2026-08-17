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
    IReadOnlyList<PlannedCostDto>? Costs = null,
    // R1 informative debt (debt buckets only): interest paid to date and interest still to pay from today, the
    // installment due-day, and whether paid-interest is an estimate (no origination date recorded).
    decimal? DebtPaidInterest = null,
    decimal? DebtRemainingInterest = null,
    int? DebtInstallmentDay = null,
    bool DebtPaidInterestEstimated = false,
    // R2: true when this debt's balance moves only as installments are logged here (rather than being walked
    // forward over its schedule) — the row offers "Log installment" and stops advancing on its own.
    bool DebtPaymentDriven = false,
    // Edit-form prefill (added for native parity, R2). SaveSavingBucketRequest is a full OVERWRITE, not a patch —
    // SavingBucketConfig.Apply calls SetSavingFund / ConfigureSavingGoal / SetSavingInitialAmount unconditionally.
    // A client that cannot read these back would silently clear the held-in fund, reset the alert threshold to its
    // 80% default, switch milestone notifications off, and wipe the starting balance every time a bucket is edited.
    Guid? FundId = null,
    decimal ThresholdPercent = 80m,
    bool NotifyOnMilestone = false,
    decimal InitialAmount = 0m,
    // The account's emergency fund, and the monthly essential-spend figure its goal is derived from — sent so the
    // client can show the basis rather than an unexplained number.
    bool IsEmergencyFund = false,
    decimal? EmergencyMonthlyEssentials = null,
    // A lease's residual/balloon. Part of the edit-form prefill too — the upsert is a full overwrite, so a client
    // that can't read it back would silently clear it and the payoff date would jump months out again.
    decimal DebtResidual = 0m);

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
    DateOnly? DebtBalanceAsOf,
    // R1: the loan's origination date (edit-form prefill), if recorded — null means paid-interest is estimated.
    DateOnly? DebtStartDate = null);

/// <summary>One manual "Add to savings" deposit this period (editable/removable), the bucket name resolved.</summary>
public record SavingDepositRowDto(Guid Id, Guid BucketId, string BucketName, decimal Amount, DateOnly Date, string? Note);

/// <summary>
/// One movement of money that is already saved: deploying a bucket to a fund, maturing it into a budget, or moving
/// it to another bucket. Distinct from a <see cref="SavingDepositRowDto"/>, which is money arriving.
/// <para>
/// ★ These existed only as commands until this shipped. Three endpoints could create a movement and one could undo
/// it, but no read returned any — so a thin client had nothing to draw an undo control against, and the delete route
/// was unreachable by construction. The thick client did not notice because it reads the allocations straight out of
/// the snapshot it carries.
/// </para>
/// </summary>
/// <param name="Kind">One of <c>disbursed</c>, <c>to-budget</c>, <c>transfer-out</c>, <c>transfer-in</c>,
/// <c>spent</c>.</param>
/// <param name="Amount">Always positive — the direction is carried by <paramref name="Kind"/>, not by the sign, so a
/// client never has to decide whether a minus means "out of this bucket" or "off the total".</param>
/// <param name="Counterpart">Where it went or came from: the budget category, the other bucket, or null for a
/// disbursement (which leaves savings altogether).</param>
/// <param name="Undoable">Whether <c>DELETE /savings/movements/{id}</c> will actually accept it — the server
/// answers this rather than each client guessing from the kind. A <c>spent</c> row is <b>not</b> undoable here: it
/// is undone by deleting the expense that caused it, and offering a control whose only outcome is a 400 is worse
/// than offering none. A <c>transfer-in</c> would succeed, but it is the outgoing half's reversal wearing a second
/// button, so only one of the pair carries it.</param>
public record SavingMovementRowDto(
    Guid Id,
    Guid BucketId,
    string BucketName,
    string Kind,
    decimal Amount,
    DateOnly Date,
    string? Note,
    string? Counterpart,
    bool Undoable);

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
    IReadOnlyList<SavingDepositRowDto> Deposits,
    // Trailing-optional so an older client deserializes this payload unchanged.
    IReadOnlyList<SavingMovementRowDto>? Movements = null)
{
    /// <summary>This period's movements of already-saved money, newest first — never null, because a period with
    /// no movements is an empty activity list rather than a missing one.</summary>
    public IReadOnlyList<SavingMovementRowDto> MovementRows => Movements ?? [];

    public static readonly SavingsViewDto Empty = new(0, "", AccountOverviewDto.Empty, 0m, 0m, [], [], []);
}

/// <summary>The delta a savings mutation returns: new <see cref="Version"/>, affected entity id, and the whole
/// refreshed <see cref="View"/> the client reconciles from. A superset of <see cref="MutationResultDto"/>.</summary>
public record SavingsMutationDto(long Version, Guid? EntityId, SavingsViewDto View);
