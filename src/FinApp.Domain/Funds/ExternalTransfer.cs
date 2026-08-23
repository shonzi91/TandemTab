using FinApp.Domain.Common;

namespace FinApp.Domain.Funds;

/// <summary>
/// Money leaving one account's fund for another account (e.g. Personal → Shared). On the source side it's a
/// real outflow — it reduces the fund's position and the period's closing balance (unlike a same-account
/// <see cref="FundTransfer"/>, which is total-preserving). On the destination side the money arrives as a
/// normal member contribution, recorded separately in that account's current period.
/// </summary>
public sealed class ExternalTransfer : Entity
{
    public Guid FundId { get; private set; }
    public Money Amount { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>The account the money was sent to (informational; the matching deposit lives in that account).</summary>
    public Guid? ToAccountId { get; }
    public string? Note { get; private set; }

    /// <summary>True when the source fund was synced (bank-mirrored) at creation, so this outflow doesn't
    /// reduce the fund's balance (the real bank balance handles it). See <see cref="Fund.IsSynced"/>.</summary>
    public bool FundSynced { get; private set; }

    public ExternalTransfer(Guid fundId, Money amount, DateOnly date, Guid? toAccountId = null, string? note = null)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new ArgumentException("Transfer amount must be positive.", nameof(amount));
        FundId = fundId;
        Amount = amount;
        Date = date;
        ToAccountId = toAccountId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void SetFundSynced(bool synced) => FundSynced = synced;

    /// <summary>
    /// Shared id tying this outflow to the deposit it created in <see cref="ToAccountId"/>, so either side can
    /// find its counterpart — the same trick <c>Expense.SettlementId</c> uses for settlements.
    /// </summary>
    /// <remarks>
    /// <b>Null on every transfer written before this existed</b>, and that is load-bearing rather than a gap to
    /// backfill: a legacy pair cannot be identified with any confidence (two same-day, same-amount transfers to
    /// one account are indistinguishable), and guessing would delete the wrong deposit. Unlinked rows therefore
    /// keep the old one-sided behaviour, and the UI says so instead of pretending.
    /// </remarks>
    public Guid? AccountTransferId { get; private set; }

    /// <summary>Body data, so a setter rather than a ctor parameter — see <see cref="AccountTransferId"/>.</summary>
    public void SetAccountTransferLink(Guid? transferId) => AccountTransferId = transferId;

    /// <summary>
    /// Optionally, the budget category this outflow is planned under. Null — the default and the ordinary case —
    /// means it counts in "money out" but against no budget, exactly as before.
    ///
    /// <para><b>★ Why a category on a transfer at all.</b> This money already counts as spending everywhere the
    /// app totals it: <c>Period.AccountTransfersOutTotal</c> is added to the Home "Spent" tile, and the Breakdown
    /// gives it a slice. Budgets were the one place it went missing, because a budget is a per-category cap and a
    /// transfer had no category — so a standing €400 to the household account sat inside "Spent" while belonging to
    /// no plan. Naming a category is what lets it be planned for, and it stays optional because most transfers
    /// (moving your own money about) are not spending anyone budgets.</para>
    ///
    /// Body data — rides in the account snapshot, no schema change.
    /// </summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Set (or clear, with null) the budget category this outflow is planned under.</summary>
    public void SetCategory(Guid? categoryId) => CategoryId = categoryId is { } c && c != Guid.Empty ? c : null;

    /// <summary>T0 — the idempotency key of the write that sent this money, so a retry after an ambiguous failure
    /// finds it instead of sending it twice. Same contract as <see cref="Budgeting.Expense.ClientId"/>.
    /// <para>⚠️ It is stamped on the <b>outflow only</b>, and that is enough: the pair is written in one
    /// two-account mutation, so an outflow carrying the key implies its deposit landed too. Looking for it on the
    /// source side also keeps the retry check inside the account the request is addressed to.</para>
    /// <para>Body data — rides in the snapshot, so a setter rather than a ctor parameter.</para></summary>
    public Guid? ClientId { get; private set; }

    public void SetClientId(Guid? clientId) => ClientId = clientId;

    /// <summary>Overwrite the editable fields of a transfer (both sides are edited together — see the
    /// <c>transfers-out</c> PUT endpoint, which keeps this and its counterpart deposit in step).</summary>
    public void Update(Money amount, DateOnly date, Guid fundId, string? note)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new ArgumentException("Transfer amount must be positive.", nameof(amount));
        Amount = amount;
        Date = date;
        FundId = fundId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}
