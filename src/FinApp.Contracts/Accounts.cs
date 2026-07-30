namespace FinApp.Contracts;

/// <summary>A contributor on a domain account (unified with the money-side "member").</summary>
public record MemberDto(Guid UserId, string DisplayName);

/// <summary>
/// Lightweight view of a domain account the caller can see (owns or was invited to). The full
/// budgeting graph travels as an <see cref="AccountSnapshot"/>; this is the list/header shape.
/// </summary>
public record AccountSummaryDto(
    Guid Id,
    string Name,
    string Currency,
    Guid OwnerUserId,
    bool IsOwner,
    IReadOnlyList<MemberDto> Members);

public record CreateAccountRequest(string Name, string Currency);
public record RenameAccountRequest(string Name);

/// <summary>Leave an account. When the leaver is the owner and others remain, <see cref="NewOwnerUserId"/> must
/// name the member who takes over. Ignored when the caller is the sole member (the account is archived).</summary>
public record LeaveAccountRequest(Guid? NewOwnerUserId = null);

/// <summary>Hand ownership of an account to another current member.</summary>
public record TransferOwnershipRequest(Guid NewOwnerUserId);

/// <summary>Outcome of a leave: the caller left a shared account, or (as sole member) it was archived.</summary>
public enum LeaveAccountResult { Left, Archived }

/// <summary>An archived (soft-deleted) account the caller can still restore until <see cref="PurgeAt"/>.</summary>
public record ArchivedAccountDto(Guid Id, string Name, string Currency, DateTimeOffset ArchivedAt, DateTimeOffset PurgeAt);

/// <summary>
/// Opaque-friendly full snapshot of a domain account aggregate, exchanged when a contributor loads or
/// saves a shared account. <see cref="Payload"/> is the serialized aggregate; keeping it a single blob
/// lets a later milestone swap it for an end-to-end-encrypted ciphertext without changing the contract.
/// </summary>
public record AccountSnapshot(Guid Id, long Version, string Payload);

/// <summary>Push a locally-edited snapshot back to the server. <see cref="ExpectedVersion"/> enables optimistic concurrency.</summary>
public record SaveAccountRequest(string Payload, long ExpectedVersion);

// --- Command writes (Option-A migration, docs/MOBILE.md) ---------------------------------------------------
// The client used to mutate the aggregate locally and PUT the whole snapshot; these let a thin (native) client send
// just the command — the server applies it through the same domain, so the money maths can't drift between clients.

/// <summary>Seed a freshly-created account's starter body server-side (default categories/funds + the first period),
/// so a thin client doesn't need the domain to initialize an account. <see cref="Today"/> dates the first period to
/// the caller's local month; when null the server uses its own UTC date. Fails (409) if the account is already set up.</summary>
public record BootstrapAccountRequest(DateOnly? Today = null);

/// <summary>
/// Log a new expense in the account's open period, computed server-side. The member is the caller and the fund's
/// "synced" flag is derived from the fund itself, so neither is in the request. Bank-import and on-behalf settlement
/// provenance are handled by their own flows, not here. Mirrors <c>BudgetingState.AddExpense</c>.
/// </summary>
public record AddExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null, bool OnBehalfOfOtherAccount = false, Guid? TagId = null);

/// <summary>Replace an existing expense's category/amount/fund/note/date (an append-only edit — see
/// <c>Period.EditExpense</c>). The expense id travels in the route. <see cref="TagId"/> sets the expense's single
/// tag (null clears it). One tag per expense. Mirrors <c>BudgetingState.EditExpense</c>.</summary>
public record EditExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null, Guid? TagId = null);

/// <summary>
/// Record income (a deposit) for the caller in the open period, computed server-side. The member is the caller and
/// the fund's "synced" flag is derived from the fund. <see cref="CategoryId"/> is an optional <b>contribution</b>
/// category (Salary, Vouchers…); pass <see cref="System.Guid.Empty"/> for general income. Deposits with the same
/// (member, category, fund) <b>merge</b> into one row. Mirrors <c>BudgetingState.RecordDeposit</c>.
/// </summary>
public record AddDepositRequest(Guid CategoryId, Guid FundId, decimal Amount, DateOnly Date);

/// <summary>Overwrite one of the caller's own deposit rows (amount/category/fund/date). The deposit id travels in the
/// route; a caller may only change their own deposits (else 403). Mirrors <c>BudgetingState.EditDeposit</c>.</summary>
public record EditDepositRequest(Guid CategoryId, Guid FundId, decimal Amount, DateOnly Date);

/// <summary>Set money aside into a savings bucket ("Add to savings"), computed server-side. A plain (un-noted) deposit
/// stays editable/removable; a note turns it into a non-editable annotated allocation (as in the web). Amount must be
/// positive (draw down via the spend endpoint). Mirrors <c>BudgetingState.AllocateSaving</c>.</summary>
public record AddSavingDepositRequest(Guid SavingCategoryId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Change the amount of a manual savings deposit (append-only — the row is replaced, keeping its original
/// date/bucket). The allocation id travels in the route. Mirrors <c>BudgetingState.EditSavingDeposit</c>.</summary>
public record EditSavingDepositRequest(decimal Amount);

/// <summary>
/// Spend accumulated savings: records a real expense against <see cref="CategoryId"/> plus a matching negative
/// drawdown from <see cref="SavingCategoryId"/>, so the earmark and the balance both fall. The member is the caller.
/// <see cref="FundId"/> is the fund the money physically leaves; pass <see cref="System.Guid.Empty"/> to let the
/// server pick the first spendable (non-synced) fund, matching the web default. Mirrors <c>BudgetingState.SpendFromSavings</c>.
/// </summary>
public record SpendFromSavingsRequest(Guid SavingCategoryId, Guid CategoryId, decimal Amount, DateOnly Date, Guid FundId = default, string? Note = null);

/// <summary>One planned future cost of a sinking-fund bucket (pure planning data — never moves money). <see cref="Cadence"/>
/// is "one-off"/"monthly"/"quarterly"/"yearly"; <see cref="DueDate"/> applies to a one-off target.</summary>
public record PlannedCostDto(string Label, decimal Amount, string Cadence, DateOnly? DueDate = null);

/// <summary>
/// Create or configure a savings bucket in one shot (mirrors <c>BudgetingState.AddSavingBucket</c>/<c>SaveSavingBucket</c>).
/// The bucket's <b>kind</b> is chosen by the flags, in priority order: <see cref="IsDebt"/> (payoff envelope, using
/// Debt* + re-anchored to the server date) → <see cref="IsInvestment"/> (growth envelope, using Inv*) → otherwise an
/// ordinary goal (<see cref="GoalAmount"/> &gt; 0, with <see cref="ThresholdPercent"/> alert 0–100 and
/// <see cref="NotifyOnMilestone"/>). <see cref="IsExpensesFund"/> turns it into a sinking fund for <see cref="Costs"/>
/// and clears any goal. <see cref="PlannedContribution"/>, <see cref="FundId"/> (earmark tag) and <see cref="InitialAmount"/>
/// (only honoured while the account has a single period) apply regardless of kind.
/// </summary>
public record SaveSavingBucketRequest(
    string Name,
    string? Icon = null,
    decimal? GoalAmount = null,
    decimal ThresholdPercent = 80m,
    bool NotifyOnMilestone = false,
    decimal InitialAmount = 0m,
    bool IsDebt = false,
    decimal DebtBalance = 0m,
    decimal DebtRate = 0m,
    decimal DebtInstallment = 0m,
    decimal? PlannedContribution = null,
    bool IsInvestment = false,
    decimal InvRate = 0m,
    decimal InvTermYears = 0m,
    int InvCompounds = 12,
    Guid? FundId = null,
    IReadOnlyList<PlannedCostDto>? Costs = null,
    bool IsExpensesFund = false);

/// <summary>Archive (hide) or restore a resource that supports it (e.g. a savings bucket). Reversible; keeps history.</summary>
public record SetArchivedRequest(bool Archived);

/// <summary>Deploy a savings bucket to its goal (e.g. a loan prepayment): money leaves the account from <see cref="FundId"/>,
/// recorded as an external-transfer-out (not consumption, so it doesn't hit the expenses ledger) plus a drawdown that
/// drains the bucket. On a debt bucket it also counts as an extra principal payment. Mirrors <c>BudgetingState.DisburseSaving</c>.</summary>
public record DisburseSavingRequest(Guid SavingCategoryId, Guid FundId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Mature saved money into a spendable budget for this period: release the earmark from <see cref="SavingCategoryId"/>
/// and add <see cref="Amount"/> to <see cref="CategoryId"/>'s budget (no money physically moves). Mirrors <c>BudgetingState.ConvertSavingToBudget</c>.</summary>
public record ConvertSavingToBudgetRequest(Guid SavingCategoryId, Guid CategoryId, decimal Amount, DateOnly Date, string? Note = null);

/// <summary>Move earmarked money from one savings bucket to another (net-neutral). Mirrors <c>BudgetingState.MoveSavingToBucket</c>.</summary>
public record MoveSavingsRequest(Guid FromBucketId, Guid ToBucketId, decimal Amount, DateOnly Date, string? Note = null);

// --- Account structure: spend categories, funds, contribution categories ------------------------------------

/// <summary>Add a spend category. <see cref="ParentId"/> nests it under another; <see cref="Essential"/> marks it an
/// essential spend (advisory). Mirrors <c>BudgetingState.AddCategory</c>.</summary>
public record CreateCategoryRequest(string Name, Guid? ParentId = null, string? Icon = null, bool Essential = false);

/// <summary>Edit a spend category's name and icon (a null icon clears it). <see cref="Essential"/> is applied only when
/// provided, so an edit that doesn't carry it leaves the flag untouched. Mirrors <c>BudgetingState.EditCategory</c>.</summary>
public record EditCategoryRequest(string Name, string? Icon = null, bool? Essential = null);

/// <summary>Add a fund (a place money lives). <see cref="ParentId"/> nests it as an informational sub-fund. Mirrors
/// <c>BudgetingState.AddFund</c>.</summary>
public record CreateFundRequest(string Name, Guid? ParentId = null, string? Note = null, string? Icon = null);

/// <summary>Edit a fund's name, note and icon (null note/icon clear them). Mirrors <c>BudgetingState.RenameFund</c>+note/icon.</summary>
public record EditFundRequest(string Name, string? Note = null, string? Icon = null);

/// <summary>Add a contribution (income) category (Salary, Vouchers…). Mirrors <c>BudgetingState.AddContributionCategory</c>.</summary>
public record CreateContributionCategoryRequest(string Name, string? Icon = null);

/// <summary>Edit a contribution category's name and icon (null icon clears it). Mirrors <c>BudgetingState.SaveContributionCategory</c>.</summary>
public record EditContributionCategoryRequest(string Name, string? Icon = null);

/// <summary>Add a cross-cutting tag (a flat label attached to expenses, alongside sub-categories). Rejects a duplicate
/// name. Mirrors <c>BudgetingState.AddTag</c>.</summary>
public record CreateTagRequest(string Name, string? Icon = null);

/// <summary>Rename a tag and set its icon (a null icon clears it). Mirrors <c>BudgetingState.SaveTag</c>.</summary>
public record EditTagRequest(string Name, string? Icon = null);

// --- Recurring items (bills / income expectations) ----------------------------------------------------------

/// <summary>
/// Add a recurring expectation (a bill or regular income). <see cref="Kind"/> is "expense"/"income";
/// <see cref="Mode"/> is "fixed" (same every time), "typical" (a self-tuning estimate) or "reminder" (no amount,
/// just prompts). <see cref="CategoryId"/> is a spend category for an expense, a contribution category for income;
/// <see cref="DayOfMonth"/> is 1–28. <see cref="AutoPost"/> only applies to a fixed amount. The item is stamped with
/// the server date so it can't fall due before it existed. Mirrors <c>BudgetingState.AddRecurring</c>.
/// </summary>
public record AddRecurringRequest(string Name, string Kind, string Mode, decimal Expected, int DayOfMonth,
    Guid CategoryId, Guid FundId, string? Icon = null, bool AutoPost = false);

/// <summary>Edit a recurring item (its kind can't change). Fields as in <see cref="AddRecurringRequest"/>. Mirrors
/// <c>BudgetingState.UpdateRecurring</c>.</summary>
public record UpdateRecurringRequest(string Name, string Mode, decimal Expected, int DayOfMonth,
    Guid CategoryId, Guid FundId, string? Icon = null, bool AutoPost = false);

/// <summary>Pause or resume a recurring item (a paused item never falls due).</summary>
public record SetActiveRequest(bool Active);

/// <summary>Confirm a due recurring item with the real amount: posts a normal expense/income, nudges a "typical"
/// estimate toward the actual, and marks it handled for this period. A zero amount skips (marks handled, posts
/// nothing). Mirrors <c>BudgetingState.ConfirmRecurring</c>.</summary>
public record ConfirmRecurringRequest(decimal ActualAmount);

// --- Settlement / cross-account (money or an expense moved between two of the caller's accounts) ---------------

/// <summary>Send money from a fund on this account to another account the caller also belongs to (same currency): an
/// outflow (external transfer) here and a matching deposit there, in one atomic two-account save. Capped at the source
/// fund's balance. <see cref="DestinationFundId"/> empty picks the destination's first unsynced fund; <see cref="Date"/>
/// defaults to the server date. Mirrors <c>BudgetingState.TransferToAccount</c>.</summary>
public record TransferToAccountRequest(Guid DestinationAccountId, Guid FromFundId, decimal Amount,
    Guid DestinationFundId = default, string? Note = null, DateOnly? Date = null);

/// <summary>Settle (or re-settle) part of an "on behalf of another account" expense onto another account: the amount
/// becomes that account's own expense (in the picked fund + category) and this expense is reduced by it, the two linked
/// by a settlement id so either side's edits keep the other in step. Capped at the expense's original amount. Empty
/// destination fund/category resolve to the destination's defaults. Mirrors <c>BudgetingState.SettleExpenseToAccount</c>.</summary>
public record SettleExpenseRequest(Guid DestinationAccountId, Guid DestinationFundId, Guid DestinationCategoryId,
    decimal Amount, string? Note = null);

// --- Statement import (reviewed rows -> real expenses & income in one save) -----------------------------------

/// <summary>One reviewed statement row to import. A <b>negative</b> <see cref="Amount"/> posts an expense (its
/// absolute value) with <see cref="CategoryId"/> read as a spend category; a <b>positive</b> one posts income with
/// <see cref="CategoryId"/> read as a contribution category. Both attribute to <see cref="FundId"/>. Rows with a zero
/// amount or an empty category/fund are skipped. Mirrors a row of <c>BudgetingState.ImportTransactions</c>.</summary>
public record ImportRowDto(decimal Amount, DateOnly Date, Guid CategoryId, Guid FundId, string? Note = null);

/// <summary>Import a batch of reviewed statement rows in one save (all-or-nothing — a row that names a missing
/// category/fund fails the whole batch with 400). When <see cref="SkipDuplicates"/> is true (the default), a row that
/// matches an entry <b>already</b> in the target period (same date + amount + fund) is skipped, so re-importing the
/// same statement is safe; duplicates <i>within</i> one batch still post (they're compared against pre-existing data
/// only). Mirrors <c>BudgetingState.ImportTransactions</c> + the web's <c>ImportLooksDuplicate</c> hint.</summary>
public record ImportTransactionsRequest(IReadOnlyList<ImportRowDto> Rows, bool SkipDuplicates = true);

/// <summary>Result of a statement import: the new snapshot <see cref="Version"/>, how many rows posted
/// (<see cref="Imported"/>), how many were skipped as zero/empty (<see cref="Skipped"/>), and how many were skipped
/// as <see cref="Duplicates"/> of existing entries.</summary>
public record ImportResultDto(long Version, int Imported, int Skipped, int Duplicates = 0);

// --- Reallocation (move a budget's spare toward savings or another budget) ------------------------------------

/// <summary>The web's one-step "Move it to the loan" nudge: trim a category's budget to <see cref="NewBudget"/> (an
/// absolute new allocation, with its alert <see cref="ThresholdPercent"/> 0–100 and <see cref="NotifyEvery"/>) and set
/// <see cref="Amount"/> aside toward the <see cref="SavingCategoryId"/> bucket — one save, so the spare disappears and
/// the earmark grows together. Advisory: saving beyond free cash is allowed. <see cref="Date"/> defaults to the server
/// date. Mirrors <c>BudgetingState.ReallocateBudgetToSaving</c>.</summary>
public record ReallocateToSavingsRequest(Guid CategoryId, decimal NewBudget, decimal ThresholdPercent, bool NotifyEvery,
    Guid SavingCategoryId, decimal Amount, DateOnly? Date = null);

/// <summary>Move the unspent part of one budget into another (both in the open period). Capped at the source budget's
/// leftover (allocated − spent) so it can't be cut below what's already been spent; the amount must be positive and the
/// categories differ. Exposes the domain <c>BudgetReallocationService.ToBudget</c> (no web UI yet).</summary>
public record ReallocateToBudgetRequest(Guid FromCategoryId, Guid ToCategoryId, decimal Amount);

// --- Fund transfers + opening balances (intra-account money placement) ----------------------------------------

/// <summary>Set a fund's opening balance for the open period (what it held at the period's start). Overwrites any
/// existing opening for that fund. Mirrors <c>BudgetingState.SetFundOpeningBalance</c>.</summary>
public record SetFundOpeningBalanceRequest(decimal Amount);

/// <summary>Move money between two funds within the account. Intra-account moves are total-preserving (only where the
/// money sits changes), so the source may go negative — no balance cap. The funds must differ and the amount be
/// positive. <see cref="Date"/> defaults to the server date. Synced sides are recorded but not moved (the real bank
/// balance is authoritative). Mirrors <c>BudgetingState.TransferFunds</c>.</summary>
public record TransferFundsRequest(Guid FromFundId, Guid ToFundId, decimal Amount, DateOnly? Date = null, string? Note = null);

/// <summary>Edit a fund transfer (its original date is preserved). Fields as in <see cref="TransferFundsRequest"/>,
/// minus the date. Bank provenance is kept but the auto-filed badge is cleared. Mirrors
/// <c>BudgetingState.EditFundTransfer</c>.</summary>
public record EditFundTransferRequest(Guid FromFundId, Guid ToFundId, decimal Amount, string? Note = null);

// --- Budgets (per-category spending plans within a period) ----------------------------------------------------

/// <summary>Create or update the budget for a category in the open period (idempotent — no separate add vs. edit).
/// <see cref="Amount"/> is the planned allocation; <see cref="ThresholdPercent"/> (0–100, default 80) is the spend
/// level that fires an alert; <see cref="NotifyEvery"/> pings on every expense. Budgets are advisory and never
/// capped (only savings reserves real cash). Mirrors <c>BudgetingState.SaveBudget</c>.</summary>
public record SetBudgetRequest(decimal Amount, decimal ThresholdPercent = 80m, bool NotifyEvery = false);

// --- Period lifecycle (roll into the next period / reschedule / undo the last) --------------------------------

/// <summary>
/// Roll into the next period: closes the current one and opens the next (its <c>From</c> = the day after the current
/// <c>To</c>, running one calendar month). Only allowed once the current period has actually ended (<c>To</c> before
/// <see cref="Today"/>) — this blocks farming future periods. <see cref="CopyBudgets"/> carries the current budgets
/// forward; <see cref="AdjustBudgets"/> (only with <see cref="CopyBudgets"/>) nudges each toward what was spent.
/// <see cref="FundOpenings"/> is each top-level fund's real current balance, which becomes the new period's opening
/// balance (funds omitted open at zero). <see cref="SyncedFundClosingBalance"/> is the live bank balance captured for
/// a <b>synced</b> fund — stored as an informative-only opening (never entered by hand; the server can't read it, so
/// the caller supplies it or it's skipped). <see cref="Today"/> is the caller's local date (server UTC when omitted).
/// Mirrors <c>BudgetingState.StartNextPeriod</c>.
/// </summary>
public record StartNextPeriodRequest(
    bool CopyBudgets = false,
    bool AdjustBudgets = false,
    IReadOnlyDictionary<Guid, decimal>? FundOpenings = null,
    decimal? SyncedFundClosingBalance = null,
    DateOnly? Today = null);

/// <summary>Reschedule a period's date range; every later period shifts to stay contiguous, each keeping its length.
/// The target period is identified positionally (oldest = 0), matching the web's period navigation. Mirrors
/// <c>BudgetingState.ReschedulePeriod</c>.</summary>
public record ReschedulePeriodRequest(DateOnly From, DateOnly To);

/// <summary>Result of a command write: the account's new snapshot <see cref="Version"/> (so the caller keeps optimistic
/// concurrency in step) and the affected entity's id — the newly-created id on an add, the target id echoed otherwise.</summary>
public record MutationResultDto(long Version, Guid? EntityId);

/// <summary>
/// The Home balance-header figures, computed <b>server-side</b> from the account snapshot — the first read
/// moved off the client under the Option-A migration (docs/MOBILE.md). Amounts are plain decimals in
/// <see cref="Currency"/>. <see cref="Saved"/> = current − free (savings earmarked, this period + prior);
/// <see cref="SafeAfterBills"/> = free − known recurring bills still due this period, and may be negative.
/// </summary>
public record AccountOverviewDto(
    string Currency,
    decimal Current,
    decimal Free,
    decimal Saved,
    decimal Spent,
    decimal Contributed,
    decimal BillsDue,
    decimal SafeAfterBills)
{
    public static readonly AccountOverviewDto Empty = new("", 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// The cash-runway line on Home, computed server-side (Option-A migration). Returned as <c>null</c> when there's
/// no trustworthy basis to project from (the UI shows no runway). <see cref="Months"/> is the horizon actually
/// projected; <see cref="FirstShortfallMonth"/> is the first month the balance ends below zero, or null if none
/// does. <see cref="BasedOnRecurring"/> distinguishes the young-account fallback (declared recurring bills) from
/// the normal basis (an average of the last <see cref="CompletedPeriodCount"/> closed months).
/// <para>
/// <see cref="OpeningBalance"/>, <see cref="FromMonth"/> and <see cref="MonthlyCommitted"/> are the remaining inputs
/// <c>FinApp.Forecasting.CashFlowForecast.Project</c> needs, so a thin client can reconstruct the whole month-by-month
/// series <b>and</b> re-run the "what if I spent €X less?" slider entirely client-side (basis = <c>Recurring</c> when
/// <see cref="BasedOnRecurring"/>, else <c>Demonstrated</c>) — no per-tick server round-trip.
/// </para>
/// </summary>
public record RunwayDto(
    string Currency,
    int Months,
    DateOnly? FirstShortfallMonth,
    decimal MonthlyIncome,
    decimal MonthlySpending,
    bool BasedOnRecurring,
    int CompletedPeriodCount,
    bool HasUnknownAmounts,
    decimal OpeningBalance,
    DateOnly FromMonth,
    decimal MonthlyCommitted);

/// <summary>
/// The Home "on track for" targets, computed server-side (Option-A migration). Empty when there's nothing to project
/// (the client hides the card). Each target is a debt-free date or a savings goal: <see cref="TargetDto.Kind"/> is
/// "debt-free" or "goal". For a goal, <see cref="TargetDto.Name"/> is the bucket name and <see cref="TargetDto.Icon"/>
/// its stored icon; for debt-free both are empty/null (the client supplies the localized label + flag icon). A
/// reached goal has <see cref="TargetDto.Reached"/> true and <see cref="TargetDto.Months"/> 0.
/// </summary>
public record TargetsDto(IReadOnlyList<TargetDto> Targets)
{
    public static readonly TargetsDto Empty = new([]);
}

public record TargetDto(string Kind, string Name, string? Icon, int Months, bool Reached);

/// <summary>
/// The Home milestone tallies, computed server-side (Option-A migration): how many are <see cref="Earned"/>, the
/// <see cref="Total"/> in the catalogue, and how many are <see cref="InProgress"/> (locked but above 0% — the set the
/// Home "Milestones in progress" strip draws). The full localized catalogue stays a client concern.
/// </summary>
public record MilestonesDto(int Earned, int Total, int InProgress)
{
    public static readonly MilestonesDto Empty = new(0, 0, 0);
}

/// <summary>
/// The Insights health read for the account's latest period, computed server-side (Option-A migration). Carries the
/// <b>structural</b> figures — the gauge <see cref="Score"/> (0–100) and <see cref="Band"/> ("at-risk"/"average"/
/// "healthy"), the savings rate vs target + shortfall, the outgoings trend, and the per-category breakdown — plus the
/// <b>narrative</b> as language-independent <see cref="InsightMessageDto"/>s (code + args): the verdict, the summary
/// fragments, the signal cards, the savings critique, the quick wins, the trend note and the mini-trends. Each client
/// owns the per-language templates and formats the args locally, so no language is baked into the payload.
/// <see cref="Summary"/> and <see cref="SavingsCritique"/> are ordered fragments a client joins with a space.
/// <see cref="HasData"/> is false (and everything zeroed) when the period has nothing to score yet.
/// </summary>
public record InsightsDto(
    bool HasData,
    int Score,
    int? ScoreDelta,
    string Band,
    decimal? SavingsRate,
    decimal SavingsTarget,
    decimal? SavingsShortfall,
    bool TrendUp,
    decimal TrendAverage,
    decimal TrendAvgFraction,
    InsightMessageDto Verdict,
    IReadOnlyList<InsightMessageDto> Summary,
    IReadOnlyList<InsightMessageDto> SavingsCritique,
    InsightMessageDto TrendNote,
    IReadOnlyList<InsightSignalDto> Signals,
    IReadOnlyList<InsightCategoryDto> Breakdown,
    IReadOnlyList<InsightTrendPointDto> Trend,
    IReadOnlyList<InsightMiniTrendDto> MiniTrends,
    IReadOnlyList<InsightMessageDto> QuickWins)
{
    public static readonly InsightMessageDto EmptyVerdict = new("verdict.average", []);
    public static readonly InsightMessageDto EmptyTrendNote = new("trend.none", []);
    public static readonly InsightsDto Empty = new(
        false, 0, null, "average", null, 0.20m, null, false, 0m, 0m,
        EmptyVerdict, [], [], EmptyTrendNote, [], [], [], [], []);
}

/// <summary>One row of the Insights spending breakdown. <see cref="Icon"/> is the category's raw stored icon (the
/// client resolves it to a display icon); <see cref="Dir"/> is "up"/"down"/"flat" vs the prior period.</summary>
public record InsightCategoryDto(string Name, string? Icon, decimal Amount, decimal BarFraction, string Dir);

/// <summary>One bar of the Insights outgoings trend (one recent period).</summary>
public record InsightTrendPointDto(string Label, decimal Outgoings, decimal BarFraction, bool IsCurrent);

/// <summary>One fill value for an <see cref="InsightMessageDto"/> template. <see cref="Kind"/> is "text"/"money"/
/// "percent"/"int": text = verbatim <see cref="Text"/>; money = <see cref="Number"/> as a currency amount (account
/// currency); percent = <see cref="Number"/> as a 0..1 ratio rendered as a whole percent; int = <see cref="Number"/>
/// as an already-rounded whole number.</summary>
public record InsightArgDto(string Kind, decimal Number, string? Text);

/// <summary>A language-independent narrative fragment: a stable <see cref="Code"/> naming the template and the
/// <see cref="Args"/> that fill it. The client maps the code to a localized template and formats the args.</summary>
public record InsightMessageDto(string Code, IReadOnlyList<InsightArgDto> Args);

/// <summary>An Insights signal card. <see cref="Kind"/> is "warn"/"good"/"info"; <see cref="Dir"/> is "up"/"down"/"flat".
/// <see cref="Title"/>, <see cref="Desc"/> and the <see cref="Delta"/> badge are localizable messages.</summary>
public record InsightSignalDto(string Kind, InsightMessageDto Title, InsightMessageDto Desc, InsightMessageDto Delta, string Dir);

/// <summary>One "trends over time" mini-trend (#9). <see cref="Icon"/> is the raw icon (emoji or a category's stored icon,
/// which the client resolves); <see cref="Points"/> is the chronological sparkline series; <see cref="Dir"/> is
/// "up"/"down"/"flat" pre-framed as sentiment. <see cref="Label"/>, <see cref="CurrentText"/> and <see cref="DeltaNote"/>
/// are localizable messages.</summary>
public record InsightMiniTrendDto(InsightMessageDto Label, string? Icon, IReadOnlyList<decimal> Points, InsightMessageDto CurrentText, InsightMessageDto DeltaNote, string Dir);
