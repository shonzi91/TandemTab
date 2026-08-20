using FinApp.Domain.Common;

namespace FinApp.Domain.Funds;

/// <summary>
/// A dated movement of money from one fund to another within a period. Total-preserving by
/// construction (it only changes where money sits, never how much), so it never affects a period's
/// closing balance or reconciliation — it only shifts the per-fund positions.
/// </summary>
public sealed class FundTransfer : Entity
{
    public Guid FromFundId { get; }
    public Guid ToFundId { get; }
    public Money Amount { get; }
    public DateOnly Date { get; }
    public string? Note { get; }

    /// <summary>Per-side markers: true when that fund was a synced (bank-mirrored) fund at creation, so its
    /// balance is not moved by this transfer (the real bank balance handles it). See <see cref="Fund.IsSynced"/>.</summary>
    public bool FromSynced { get; private set; }
    public bool ToSynced { get; private set; }

    public FundTransfer(Guid fromFundId, Guid toFundId, Money amount, DateOnly date, string? note = null)
    {
        if (fromFundId == toFundId)
            throw new ArgumentException("A transfer must be between two different funds.", nameof(toFundId));
        if (amount.IsNegative || amount.IsZero)
            throw new ArgumentException("Transfer amount must be positive.", nameof(amount));
        FromFundId = fromFundId;
        ToFundId = toFundId;
        Amount = amount;
        Date = date;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void SetSyncedSides(bool fromSynced, bool toSynced) { FromSynced = fromSynced; ToSynced = toSynced; }

    /// <summary>When set, the id of the bank transaction this transfer was imported from — provenance and a dedupe
    /// key so a later re-sync doesn't re-add it. Null for manual transfers.</summary>
    public string? BankExternalId { get; private set; }

    /// <summary>True when this transfer was created automatically by a saved money-in rule on bank sync (not
    /// individually reviewed), so the UI can badge it.
    /// <para>★ It <b>survives an edit</b> (S111). It used to be cleared on one, which meant correcting an amount
    /// erased the row's provenance badge and, with it, the edit form's shortcut to the rule that filed it — at
    /// exactly the moment you are correcting what that rule got wrong. What is true is that the row arrived
    /// automatically; editing it does not change where it came from.</para></summary>
    public bool AutoFiled { get; private set; }

    /// <summary>Record (or clear) this transfer's bank origin. See <see cref="BankExternalId"/> and <see cref="AutoFiled"/>.</summary>
    public void SetBankLink(string? externalId, bool autoFiled)
    {
        BankExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        AutoFiled = autoFiled;
    }
}
