namespace FinApp.Contracts;

// --- Path-B thin-client slice (docs/MOBILE.md) --------------------------------------------------------------
// A vertical slice proving a *thin* client for the Spending surface: the client holds these read-model DTOs and
// renders them directly — no on-device domain, no snapshot to deserialize. Mutations return a delta (below) so the
// client reconciles its cache from a small response instead of re-fetching the ~260KB snapshot. Display fields
// (category name/icon, fund name) are resolved server-side so a thin client needs no lookups of its own.

/// <summary>One spending row with its display fields resolved server-side. <see cref="CategoryIcon"/> is the
/// category's raw stored icon (the client applies its own name-based fallback for display); the settlement/savings
/// flags let the row badge itself without the client knowing the money model.</summary>
public record ExpenseDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string? CategoryIcon,
    Guid FundId,
    string FundName,
    decimal Amount,
    DateOnly Date,
    string? Note,
    bool AutoFiled,
    bool FromSavings,
    bool OnBehalfOfOtherAccount,
    bool IsSettlementSource,
    bool IsSettlementDestination);

/// <summary>A spend category as a picker option — id, label, stored icon, and parent for indentation. No money.</summary>
public record CategoryOptionDto(Guid Id, string Name, string? Icon, Guid? ParentId);

/// <summary>A fund as a picker option. <see cref="Synced"/> funds are bank-driven and excluded from manual pickers.</summary>
public record FundOptionDto(Guid Id, string Name, bool Synced);

/// <summary>The whole Spending surface in one read — the thin client's initial load: the current period's expenses,
/// the balance-header figures, and the category/fund options its pickers need. <see cref="Version"/> is the account
/// snapshot version, carried so the client can tell a delta apart from a stale response.</summary>
public record SpendingViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    IReadOnlyList<ExpenseDto> Expenses,
    IReadOnlyList<CategoryOptionDto> Categories,
    IReadOnlyList<FundOptionDto> Funds)
{
    public static readonly SpendingViewDto Empty = new(0, "", AccountOverviewDto.Empty, [], [], []);
}

/// <summary>The delta an expense mutation returns: the new snapshot <see cref="Version"/>, the affected row's id, the
/// authoritative <see cref="Expense"/> (null on a delete), and the recomputed <see cref="Overview"/>. Enough for the
/// client to reconcile its cache with no re-fetch. A structural superset of <see cref="MutationResultDto"/> (same
/// <c>Version</c>/<c>EntityId</c> lead), so a caller that only wants those two deserializes it unchanged.</summary>
public record ExpenseMutationDto(long Version, Guid? EntityId, ExpenseDto? Expense, AccountOverviewDto Overview);

/// <summary>The delta an income (deposit) mutation returns: new <see cref="Version"/>, the deposit row's id, and the
/// recomputed bank-adjusted <see cref="Overview"/> (deposits move Contributed/Current/Free, not the expense list).
/// A superset of <see cref="MutationResultDto"/>. Lets a thin Home/Spending reflect income without a re-fetch.</summary>
public record DepositMutationDto(long Version, Guid? EntityId, AccountOverviewDto Overview);
