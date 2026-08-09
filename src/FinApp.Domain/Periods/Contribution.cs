using FinApp.Domain.Common;

namespace FinApp.Domain.Periods;

/// <summary>
/// A member's deposit into a period — how much they have actually put in, classified by an optional
/// contribution <see cref="CategoryId"/> (Salary, Vouchers…) and attributed to a <see cref="FundId"/>
/// (the deposited money lands in that fund). <b>One row per deposit</b>: two salary payments in a month are two
/// rows, each with its own date, exactly as two expenses would be. There are no pledges or due dates.
/// </summary>
public sealed class Contribution : Entity
{
    public Guid MemberId { get; }
    public Guid CategoryId { get; private set; }
    public Guid FundId { get; private set; }
    public DateOnly Date { get; private set; }
    public Money Paid { get; private set; }

    /// <summary>True when the destination fund was synced (bank-mirrored) at creation, so this deposit doesn't
    /// add to the fund's balance (the real bank balance handles it). See <see cref="Funds.Fund.IsSynced"/>.</summary>
    public bool FundSynced { get; private set; }

    public void SetFundSynced(bool synced) => FundSynced = synced;

    /// <summary>When set, this deposit is the receiving half of a transfer from another account: the shared id of
    /// the <see cref="Funds.ExternalTransfer"/> that created it. Null on every ordinary deposit, and null on
    /// transfers recorded before the link existed (see that property's remarks — legacy pairs are not guessed at).</summary>
    public Guid? AccountTransferId { get; private set; }

    /// <summary>The account the money came from, on a deposit created by a transfer. Null otherwise.</summary>
    public Guid? FromAccountId { get; private set; }

    /// <summary>True when this deposit is one half of an account-to-account transfer and can find its other half.</summary>
    public bool IsTransferIn => AccountTransferId is not null;

    /// <summary>Body data (rides in the snapshot, ignored by EF), so a setter rather than ctor parameters.</summary>
    public void SetAccountTransferLink(Guid? transferId, Guid? fromAccountId)
    {
        AccountTransferId = transferId;
        FromAccountId = fromAccountId;
    }

    public Contribution(Guid memberId, Money paid, Guid categoryId = default, Guid fundId = default, DateOnly date = default)
    {
        if (paid.IsNegative)
            throw new ArgumentException("Deposited amount cannot be negative.", nameof(paid));
        MemberId = memberId;
        Paid = paid;
        CategoryId = categoryId;
        FundId = fundId;
        Date = date;
    }

    public void RecordPayment(Money amount)
    {
        if (amount.IsNegative)
            throw new ArgumentException("Deposit cannot be negative.", nameof(amount));
        Paid += amount;
    }

    /// <summary>Overwrite the deposited total (used when a member edits or clears their deposit).</summary>
    public void SetPaid(Money amount)
    {
        if (amount.IsNegative)
            throw new ArgumentException("Deposited amount cannot be negative.", nameof(amount));
        Paid = amount;
    }

    /// <summary>Overwrite all editable fields of a deposit row.</summary>
    public void Update(Money amount, Guid categoryId, Guid fundId, DateOnly date)
    {
        SetPaid(amount);
        CategoryId = categoryId;
        FundId = fundId;
        Date = date;
    }
}
