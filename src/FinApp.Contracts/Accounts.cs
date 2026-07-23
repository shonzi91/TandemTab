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
public record AddExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null, bool OnBehalfOfOtherAccount = false);

/// <summary>Replace an existing expense's category/amount/fund/note/date (an append-only edit — see
/// <c>Period.EditExpense</c>). The expense id travels in the route. Mirrors <c>BudgetingState.EditExpense</c>.</summary>
public record EditExpenseRequest(Guid CategoryId, decimal Amount, Guid FundId, DateOnly Date, string? Note = null);

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
/// </summary>
public record RunwayDto(
    string Currency,
    int Months,
    DateOnly? FirstShortfallMonth,
    decimal MonthlyIncome,
    decimal MonthlySpending,
    bool BasedOnRecurring,
    int CompletedPeriodCount,
    bool HasUnknownAmounts);

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
