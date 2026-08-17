namespace FinApp.Contracts;

// --- Path-B thin-client slice: Wallets / Funds (docs/MOBILE.md) ----------------------------------------------
// The "where your money lives" surface, thin. The client renders funds + their balances + this period's transfers
// from these DTOs — no domain, no snapshot. Balances are computed server-side (FundBalance handles synced funds),
// so the client never runs the money model. Mutations return a FundMutationDto whose View is the whole (bounded,
// current-period) surface refreshed — small enough to be the delta the client reconciles from.

/// <summary>One fund row with its computed <see cref="Balance"/> and period <see cref="OpeningBalance"/>. A
/// <see cref="Synced"/> fund is bank-driven (its balance is externally authoritative and it's excluded from manual
/// transfer pickers). <see cref="Icon"/> is the raw stored icon (the client applies its name-based fallback).
/// <see cref="AvailableToTransferOut"/> is the most that can be sent from this fund to another account without
/// breaking the savings earmark (≤ the fund's balance) — the cap the thick send-money modal validates against.</summary>
public record FundRowDto(
    Guid Id,
    string Name,
    string? Icon,
    string? Note,
    decimal Balance,
    decimal OpeningBalance,
    bool Synced,
    bool Archived,
    decimal AvailableToTransferOut = 0m);

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

/// <summary>
/// One transfer of money to <b>another account</b> this period — distinct from a <see cref="FundTransferRowDto"/>,
/// which moves money between wallets inside this account and never changes the total.
/// <para>
/// ★ These had no read model at all: three endpoints could create, edit and delete an account transfer, but nothing
/// returned one, so a thin client had no row to carry the <see cref="PairId"/> the edit and delete are addressed by.
/// The commands were reachable only by a client that already knew an id it had no way to learn.
/// </para>
/// </summary>
/// <param name="PairId">The link both halves carry, and the id the edit/delete endpoints take. <b>Null for a
/// transfer recorded before that link existed</b> — those can only be deleted one-sidedly, which is why this is
/// nullable rather than assumed present.</param>
/// <param name="ToAccountName">Where it went. Null when the caller can no longer see that account (they left it, or
/// it was archived) — the transfer still happened, so the row is shown rather than hidden.</param>
public record AccountTransferRowDto(
    Guid Id,
    Guid? PairId,
    Guid FromFundId,
    string FromFundName,
    Guid? ToAccountId,
    string? ToAccountName,
    decimal Amount,
    DateOnly Date,
    string? Note,
    bool Editable);

/// <summary>The whole Wallets surface in one read: the balance-header figures, the active + archived funds (each with
/// its balance), and this period's transfers. Bounded to the current period, so it stays small regardless of history.</summary>
public record WalletsViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    IReadOnlyList<FundRowDto> Funds,
    IReadOnlyList<FundRowDto> ArchivedFunds,
    IReadOnlyList<FundTransferRowDto> Transfers,
    // Trailing-optional so an older client deserializes this payload unchanged.
    IReadOnlyList<AccountTransferRowDto>? AccountTransfers = null)
{
    /// <summary>Money sent to other accounts this period, newest first — never null.</summary>
    public IReadOnlyList<AccountTransferRowDto> AccountTransferRows => AccountTransfers ?? [];

    public static readonly WalletsViewDto Empty = new(0, "", AccountOverviewDto.Empty, [], [], [], []);
}

/// <summary>The delta a fund mutation returns: the new <see cref="Version"/>, the affected entity's id, and the whole
/// refreshed <see cref="View"/> the client reconciles from (no re-fetch). A structural superset of
/// <see cref="MutationResultDto"/> (same <c>Version</c>/<c>EntityId</c> lead), so the thick client — which reads only
/// those two, and relies on <c>EntityId</c> for a new fund's id — deserializes it unchanged.</summary>
public record FundMutationDto(long Version, Guid? EntityId, WalletsViewDto View);
