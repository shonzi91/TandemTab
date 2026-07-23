namespace FinApp.Contracts;

// --- Path-B thin-client slice: Wallets / Funds (docs/MOBILE.md) ----------------------------------------------
// The "where your money lives" surface, thin. The client renders funds + their balances + this period's transfers
// from these DTOs — no domain, no snapshot. Balances are computed server-side (FundBalance handles synced funds),
// so the client never runs the money model. Mutations return a FundMutationDto whose View is the whole (bounded,
// current-period) surface refreshed — small enough to be the delta the client reconciles from.

/// <summary>One fund row with its computed <see cref="Balance"/> and period <see cref="OpeningBalance"/>. A
/// <see cref="Synced"/> fund is bank-driven (its balance is externally authoritative and it's excluded from manual
/// transfer pickers). <see cref="Icon"/> is the raw stored icon (the client applies its name-based fallback).</summary>
public record FundRowDto(
    Guid Id,
    string Name,
    string? Icon,
    string? Note,
    decimal Balance,
    decimal OpeningBalance,
    bool Synced,
    bool Archived);

/// <summary>One intra-account fund transfer this period, with both fund names resolved for display.</summary>
public record FundTransferRowDto(
    Guid Id,
    Guid FromFundId,
    string FromFundName,
    Guid ToFundId,
    string ToFundName,
    decimal Amount,
    DateOnly Date,
    string? Note);

/// <summary>The whole Wallets surface in one read: the balance-header figures, the active + archived funds (each with
/// its balance), and this period's transfers. Bounded to the current period, so it stays small regardless of history.</summary>
public record WalletsViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    IReadOnlyList<FundRowDto> Funds,
    IReadOnlyList<FundRowDto> ArchivedFunds,
    IReadOnlyList<FundTransferRowDto> Transfers)
{
    public static readonly WalletsViewDto Empty = new(0, "", AccountOverviewDto.Empty, [], [], []);
}

/// <summary>The delta a fund mutation returns: the new <see cref="Version"/>, the affected entity's id, and the whole
/// refreshed <see cref="View"/> the client reconciles from (no re-fetch). A structural superset of
/// <see cref="MutationResultDto"/> (same <c>Version</c>/<c>EntityId</c> lead), so the thick client — which reads only
/// those two, and relies on <c>EntityId</c> for a new fund's id — deserializes it unchanged.</summary>
public record FundMutationDto(long Version, Guid? EntityId, WalletsViewDto View);
