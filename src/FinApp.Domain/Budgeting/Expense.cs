using FinApp.Domain.Common;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Budgeting;

/// <summary>Which slice of a loan installment an expense represents. A logged installment posts one
/// <see cref="Principal"/> row, one <see cref="Interest"/> row, and any number of <see cref="Additional"/> rows
/// (insurance, taxes — each with its own category/tag), all sharing an <see cref="Expense.InstallmentGroupId"/>.
/// <para>
/// Separate records rather than one split expense: <see cref="Expense"/> is immutable and single-valued, and every
/// aggregation in the app (budgets, the Breakdown's per-key sums) counts one amount per entry — so linked records
/// make "show me what I actually paid in interest" work with no aggregation changes at all.
/// </para></summary>
public enum InstallmentPart { Principal = 0, Interest = 1, Additional = 2 }

/// <summary>One non-loan line riding along on an installment — insurance, a tax, a servicing fee — with its own
/// category and tag so it lands in the right budget and its own Breakdown slice.
/// <para>
/// A <b>list</b> rather than one lumped "additional" amount because real installments carry several distinct
/// charges, and collapsing them would throw away exactly the split the user logs them for.
/// </para></summary>
public sealed record InstallmentExtra(Common.Money Amount, Guid CategoryId, Guid? TagId = null, string? Note = null);

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
    public Guid CategoryId { get; private set; }
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

    /// <summary>
    /// Re-file this expense under another category. Deliberately NOT the way a user edits an expense — that is
    /// <c>Period.EditExpense</c>, which is append-only and mints a new id. This exists for one bulk maintenance
    /// case: <see cref="Accounts.Account.RemoveCategoryReassigning"/>, where a category is deleted and its history
    /// has to survive. Keeping the same expense id is the whole point there — the amount, date, member, fund,
    /// tags, installment and settlement links all stay attached to the row that already exists.
    /// </summary>
    public void MoveToCategory(Guid categoryId) => CategoryId = categoryId;

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

    /// <summary>Set on every row of a logged loan installment: the shared id tying the principal, interest and any
    /// additional rows together so they can be shown, edited and removed as one payment. Null on ordinary expenses.</summary>
    public Guid? InstallmentGroupId { get; private set; }

    /// <summary>Which slice of the installment this row is (see <see cref="InstallmentPart"/>). Null on ordinary expenses.</summary>
    public InstallmentPart? Part { get; private set; }

    /// <summary>The debt bucket this installment was paid against. Null on ordinary expenses. Note this is a
    /// <b>savings-bucket</b> link, not a savings drawdown — an installment is paid from income, so unlike
    /// <see cref="SourceSavingCategoryId"/> it draws nothing down; it only says which loan the money serviced.</summary>
    public Guid? DebtBucketId { get; private set; }

    /// <summary>True when this expense is one row of a logged installment.</summary>
    public bool IsInstallmentPart => InstallmentGroupId is not null;

    /// <summary>Mark this expense as one row of a logged installment (or clear it with nulls). A setter rather than
    /// constructor arguments because these are snapshot body data, not relational columns — and EF cannot bind an
    /// ignored property to a constructor parameter, so the ledger fields it *does* map must stay the whole ctor.</summary>
    public void SetInstallmentLink(Guid? groupId, InstallmentPart? part, Guid? debtBucketId)
    {
        InstallmentGroupId = groupId;
        Part = part;
        DebtBucketId = debtBucketId;
    }

    private readonly List<Guid> _tagIds = [];

    /// <summary>Cross-cutting <see cref="Tag"/> ids attached to this expense. Unlike the ledger fields (amount,
    /// category, date) tags are user labels that can be re-assigned in place — so this is mutable and does not
    /// mint a new entry. Ids of tags that were later hard-removed simply stop resolving; callers ignore them.</summary>
    public IReadOnlyList<Guid> TagIds => _tagIds;

    /// <summary>Replace this expense's tag set (deduped, empties dropped). Pass an empty sequence to clear all tags.
    /// One tag per expense is the model now (<see cref="TagId"/>/<see cref="SetTag"/>); this stays as the storage
    /// primitive the snapshot serializer round-trips, and legacy multi-tag snapshots are collapsed to one on load.</summary>
    public void SetTags(IEnumerable<Guid>? tagIds)
    {
        _tagIds.Clear();
        if (tagIds is null) return;
        foreach (var t in tagIds.Distinct())
            if (t != Guid.Empty) _tagIds.Add(t);
    }

    /// <summary>This expense's single tag, or null. One tag per expense — the canonical accessor over the legacy
    /// <see cref="TagIds"/> list (which is kept only for snapshot back-compat and never holds more than one now).</summary>
    public Guid? TagId => _tagIds.Count > 0 ? _tagIds[0] : null;

    /// <summary>Set (or clear, with null / <see cref="Guid.Empty"/>) this expense's single tag.</summary>
    public void SetTag(Guid? tagId) => SetTags(tagId is { } t && t != Guid.Empty ? new[] { t } : null);

    /// <summary>
    /// The <see cref="Trip"/> this expense belongs to, or null. Set on its own axis rather than by reusing the tag
    /// slot: one tag per expense is the model, and letting a trip occupy it would consume the very axis the trip
    /// recap splits by (stay / travel / food / tickets). With both, a trip expense is "Rome" *and* "hotel".
    /// <para>
    /// <b>Independent of <see cref="Date"/> and of the trip's own dates</b> — that is what lets a flight bought in
    /// March count toward a June trip while staying in March's period and March's budget. Nothing about periods,
    /// budgets or safe-to-spend keys off this field; it is a reporting link only.
    /// </para>
    /// Body data — travels in the account snapshot, not the relational header.
    /// </summary>
    public Guid? TripId { get; private set; }

    /// <summary>Attach this expense to a trip (or detach with null / <see cref="Guid.Empty"/>). A setter rather
    /// than a constructor argument because this is snapshot body data, and EF cannot bind an ignored property to a
    /// constructor parameter — the ledger fields it *does* map must stay the whole ctor.</summary>
    public void SetTrip(Guid? tripId) => TripId = tripId is { } t && t != Guid.Empty ? t : null;

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
