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

// --- Debt payoff: the forecast figures a thin client cannot compute --------------------------------------------

/// <summary>One offer a bank might make after a lump payment. <see cref="PerMonth"/> is the installment it implies
/// and <see cref="Months"/> how long it then runs; <see cref="NewInterest"/> is what that costs in total and
/// <see cref="SavedInterest"/> what it saves against carrying on unchanged.</summary>
public record PayoffOfferDto(string Kind, decimal PerMonth, int Months, decimal NewInterest, decimal SavedInterest);

/// <summary>
/// One point on the "extra per month" curve: paying <see cref="Extra"/> more each month saves
/// <see cref="MonthsSaved"/> months and <see cref="InterestSaved"/> in interest.
/// <para>
/// ⚠️ <b>The curve is precomputed as a handful of points on purpose.</b> The slider that reads it has to move
/// under a finger: a client that re-fetched per drag step would be unusable, and one that computed the maths
/// locally would be a second, untested implementation of compound interest — the thing the whole thin-client rule
/// exists to prevent. So the server does the arithmetic once, for a fixed set of steps, and the slider snaps to
/// them.
/// </para>
/// </summary>
public record PayoffCurvePointDto(decimal Extra, int MonthsSaved, decimal InterestSaved);

/// <summary>
/// What a debt bucket's future looks like, computed server-side from <c>FinApp.Forecasting.LoanForecast</c>.
/// <para>
/// This read exists because Android is a thin client that holds no domain logic: without it the phone has the
/// loan's <i>inputs</i> (balance, rate, installment) and no honest way to turn them into a payoff date. Porting
/// the amortisation into Kotlin would put a second, untested implementation of compound interest in front of
/// somebody's mortgage.
/// </para>
/// <para>
/// <see cref="Offers"/> and <see cref="Curve"/> are the <b>Pro</b> half (<c>PlanFeatures.Debt</c>) and come back
/// empty on Free — mirroring the web, where the header and the lump-sum explanation stay on Free and only the
/// modelling of the bank's alternatives is withheld. <see cref="Available"/> is false when there is no schedule to
/// walk at all (a payment-driven loan, or an installment that cannot out-run the interest), in which case every
/// figure here is zero and the client must say so rather than draw a flat line.
/// </para>
/// </summary>
public record DebtPayoffDto(
    bool Available,
    string Currency,
    decimal Balance,
    decimal Installment,
    decimal AnnualRatePercent,
    int Months,
    DateOnly? PayoffOn,
    decimal TotalInterest,
    // The one-off: "pay the X you have set aside now". Zero when nothing is set aside against this bucket.
    decimal SetAside,
    decimal LumpBalanceAfter,
    int LumpMonthsSaved,
    decimal LumpInterestSaved,
    bool LumpClearsTheLoan,
    IReadOnlyList<PayoffOfferDto> Offers,
    IReadOnlyList<PayoffCurvePointDto> Curve)
{
    public static readonly DebtPayoffDto None =
        new(false, "", 0m, 0m, 0m, 0, null, 0m, 0m, 0m, 0, 0m, false, [], []);
}

// --- The whole-stack payoff plan (R2.5 server slice, QUEUE #8) -------------------------------------------------
// ⚠️ The gap this closes, stated plainly: the server exposed only the PER-BUCKET payoff, so a thin client could
// answer "when does this loan end" and could not answer "when am I debt-free". The web has answered the second
// since the multi-debt planner shipped, computing it in the thick client from FinApp.Forecasting.LoanForecast over
// the whole snapshot — which is precisely the sort of number a second implementation gets plausibly wrong.

/// <summary>One debt in the plan's clearing order, with the month it is cleared in and the date that lands on.
/// <see cref="ClearedInMonth"/> is counted from the plan's start, so 1 is "next month".</summary>
public record PlanLoanDto(
    Guid BucketId,
    string Name,
    string? Icon,
    decimal Balance,
    decimal AnnualRatePercent,
    decimal Installment,
    int ClearedInMonth,
    DateOnly ClearedOn);

/// <summary>
/// The whole-stack payoff plan: one spare amount thrown at every debt each month on top of every installment,
/// with each cleared debt's installment rolling onto the next.
///
/// <para>★ <b>Two answers, not one, and they are asked differently.</b> <see cref="Months"/> is the PLAN — a
/// strategy plus an extra amount you are considering. <see cref="PaceMonths"/> is the FORECAST — each debt at its
/// installment plus the pace you have actually demonstrated, which is what the Home "Debt-free" line has always
/// shown. They answer "what if I did this" and "where am I heading", and a client that showed one where the other
/// belongs would be making a promise out of a hypothetical.</para>
///
/// <para>⚠️ <see cref="Available"/> is false when the stack <b>never clears</b> — installments that cannot out-run
/// the interest have no honest debt-free date, and the right response is to say so and ask for an extra amount,
/// not to print a date fifty years out. <see cref="DebtCount"/> is stated rather than acted on: the web draws this
/// card only with two or more debts (with one, the plan IS that loan's own payoff), and a client applies its own
/// threshold rather than inheriting one baked into the read.</para>
///
/// <para><see cref="MonthsSaved"/> / <see cref="InterestSaved"/> compare the plan against the same strategy with
/// <b>no</b> extra — the honest baseline for "what is the extra buying me", and zero when there is no extra.</para>
/// </summary>
public record DebtPlanDto(
    bool Available,
    string Currency,
    int DebtCount,
    /// <summary>"avalanche" (highest rate first, cheapest overall) or "snowball" (smallest balance first), echoed
    /// back so the client can render the strategy note against the figures it actually produced.</summary>
    string Strategy,
    decimal ExtraPerMonth,
    decimal TotalOwed,
    decimal TotalInstallments,
    int Months,
    DateOnly? DebtFreeOn,
    decimal TotalInterest,
    int MonthsSaved,
    decimal InterestSaved,
    IReadOnlyList<PlanLoanDto> Order,
    /// <summary>Months to debt-free at the pace actually demonstrated (installment + the average set aside per
    /// period, per debt) — the Home line's figure. Null when there are no debts, or one of them never clears at
    /// that amount, which is the same "no honest date to promise" answer <see cref="Available"/> gives.
    /// <para>⚠️ <b>It is routinely LATER than <see cref="Months"/> even at a zero extra</b>, and a client showing
    /// both must say why or the pair reads as a contradiction: the plan rolls each cleared debt's installment onto
    /// the next, and this does not — every debt simply runs its own installment out. That is the right assumption
    /// for "what happens if nothing changes", since the freed-up money usually just gets spent.</para></summary>
    int? PaceMonths,
    DateOnly? PaceDebtFreeOn,
    /// <summary>Interest the demonstrated pace saves across every debt versus paying just the installments. Zero
    /// when nothing extra is being paid, so the line only claims a saving when there is one.</summary>
    decimal PaceInterestSaved)
{
    public static readonly DebtPlanDto None =
        new(false, "", 0, "avalanche", 0m, 0m, 0m, 0, null, 0m, 0, 0m, [], null, null, 0m);
}
