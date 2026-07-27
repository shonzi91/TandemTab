using FinApp.Domain.Common;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// An immutable ledger entry: money spent in a category, from a fund, by a member, on a date.
/// Append-only — corrections are made by adding a reversing entry, which keeps multi-user
/// sync conflict-free and makes period reconciliation auditable.
/// When <see cref="SourceSavingCategoryId"/> is set, the expense was paid from a savings bucket
/// (a "saving → expense" conversion) and also draws down that saving earmark.
///
/// <para><b>Settlement (on-behalf) links</b> tie an expense paid here to a matching expense in another account:
/// the <i>source</i> side carries <see cref="SettledToAccountId"/> + <see cref="SettledAmount"/> (and its
/// <see cref="Amount"/> is reduced by what was pushed away), while the <i>destination</i> side carries
/// <see cref="SettledFromAccountId"/>. Both share a <see cref="SettlementId"/> so either side can find its
/// counterpart and keep it in step on edit/remove.</para>
/// </summary>
public sealed class Expense : Entity
{
    public Guid CategoryId { get; }
    public Money Amount { get; }
    public DateOnly Date { get; }
    public Guid MemberId { get; }
    public Guid FundId { get; }
    public string? Note { get; }
    public Guid? SourceSavingCategoryId { get; }

    /// <summary>
    /// When true, this expense was paid here but is (partly or wholly) on behalf of another account, so it can
    /// be settled — a chosen amount is pushed onto another account as that account's expense and this expense's
    /// <see cref="Amount"/> is reduced accordingly. Stays flagged even after settling so the action remains available.
    /// </summary>
    public bool OnBehalfOfOtherAccount { get; }

    /// <summary>Shared id linking a source expense to its destination counterpart in another account.</summary>
    public Guid? SettlementId { get; }

    /// <summary>On the <b>source</b> expense: the account a portion of this expense was settled onto (null otherwise).</summary>
    public Guid? SettledToAccountId { get; }

    /// <summary>On the <b>destination</b> expense: the account this expense was settled from (null otherwise).</summary>
    public Guid? SettledFromAccountId { get; }

    /// <summary>On the <b>source</b> expense: how much was pushed onto the other account (already deducted from <see cref="Amount"/>), in the account currency.</summary>
    public decimal SettledAmount { get; }

    /// <summary>True when the paying fund was synced (bank-mirrored) at creation, so this expense doesn't reduce
    /// the fund's balance (the real bank balance handles it). See <see cref="Funds.Fund.IsSynced"/>.</summary>
    public bool FundSynced { get; private set; }

    public void SetFundSynced(bool synced) => FundSynced = synced;

    /// <summary>When set, the id of the bank transaction this expense was imported from — provenance and a dedupe
    /// key so a later re-sync of the same window doesn't re-add it. Null for purely manual expenses.</summary>
    public string? BankExternalId { get; private set; }

    /// <summary>True when this expense was filed automatically by a saved merchant rule on bank sync (i.e. not
    /// individually reviewed), so the UI can badge it for the user to double-check. Cleared once they edit it.</summary>
    public bool AutoFiled { get; private set; }

    /// <summary>Record (or clear) this expense's bank origin. See <see cref="BankExternalId"/> and <see cref="AutoFiled"/>.</summary>
    public void SetBankLink(string? externalId, bool autoFiled)
    {
        BankExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        AutoFiled = autoFiled;
    }

    private readonly List<Guid> _tagIds = [];

    /// <summary>Cross-cutting <see cref="Tag"/> ids attached to this expense. Unlike the ledger fields (amount,
    /// category, date) tags are user labels that can be re-assigned in place — so this is mutable and does not
    /// mint a new entry. Ids of tags that were later hard-removed simply stop resolving; callers ignore them.</summary>
    public IReadOnlyList<Guid> TagIds => _tagIds;

    /// <summary>Replace this expense's tag set (deduped, empties dropped). Pass an empty sequence to clear all tags.</summary>
    public void SetTags(IEnumerable<Guid>? tagIds)
    {
        _tagIds.Clear();
        if (tagIds is null) return;
        foreach (var t in tagIds.Distinct())
            if (t != Guid.Empty) _tagIds.Add(t);
    }

    public Expense(
        Guid categoryId,
        Money amount,
        DateOnly date,
        Guid memberId,
        Guid fundId,
        string? note = null,
        Guid? sourceSavingCategoryId = null,
        bool onBehalfOfOtherAccount = false,
        Guid? settlementId = null,
        Guid? settledToAccountId = null,
        Guid? settledFromAccountId = null,
        decimal settledAmount = 0m)
    {
        if (amount.IsNegative)
            throw new ArgumentException("Expense amount cannot be negative.", nameof(amount));
        CategoryId = categoryId;
        Amount = amount;
        Date = date;
        MemberId = memberId;
        FundId = fundId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SourceSavingCategoryId = sourceSavingCategoryId;
        OnBehalfOfOtherAccount = onBehalfOfOtherAccount;
        SettlementId = settlementId;
        SettledToAccountId = settledToAccountId;
        SettledFromAccountId = settledFromAccountId;
        SettledAmount = settledAmount;
    }

    public bool IsFromSavings => SourceSavingCategoryId is not null;

    /// <summary>The settled amount as <see cref="Money"/> (in this expense's currency).</summary>
    public Money SettledMoney => new(SettledAmount, Amount.Currency);

    /// <summary>This expense had a portion settled onto another account (its amount is the reduced, after-settlement value).</summary>
    public bool IsSettlementSource => SettledToAccountId is not null && SettledAmount != 0m;

    /// <summary>This expense was created by settling a portion of an expense in another account.</summary>
    public bool IsSettlementDestination => SettledFromAccountId is not null;

    /// <summary>The expense's value before any settlement was pushed away (= <see cref="Amount"/> + <see cref="SettledAmount"/>).</summary>
    public Money OriginalAmount => Amount + SettledMoney;
}
