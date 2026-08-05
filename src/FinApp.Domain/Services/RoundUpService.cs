using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;

namespace FinApp.Domain.Services;

/// <summary>
/// F4 round-ups: after an expense is logged, set aside the change up to the next whole 1 or 5 into a chosen savings
/// bucket. Painless saving that feeds the "set aside €X to hit your rate" nudge the app already shows.
///
/// <para><b>Why this is a service and not a line inside AddExpense:</b> the sweep must be identical on the web client
/// (which applies the mutation optimistically) and on the server (which applies the same mutation when the request
/// lands). Both call this. If they drifted, the client would paint a savings row the server never wrote — and the
/// next snapshot refetch would silently take the money back.</para>
///
/// <para>The sweep is a <b>savings allocation, not a second expense</b>: the ledger still records exactly what was
/// spent. Free cash drops by the expense plus the round-up, which is the truth — the change is reserved, not gone.</para>
/// </summary>
public sealed class RoundUpService
{
    /// <summary>The note stamped on a swept allocation. Written (rather than left blank) so the row is identifiable
    /// in the savings list and excluded from <c>Period.IsManualDeposit</c> — a round-up is not a deposit the user
    /// made by hand, and offering it under "your manual deposits" would invite them to edit a derived figure.</summary>
    public const string SweepNote = "Round-up";

    private readonly SavingsReportService _savings = new();

    /// <summary>
    /// Sweep the round-up for an expense of <paramref name="expenseAmount"/> into the configured bucket, returning the
    /// allocation, or null when nothing was swept. Never throws for an ordinary "can't right now" reason — a round-up
    /// that fails must never fail the expense that triggered it.
    /// </summary>
    public SavingAllocation? Sweep(Account account, Period period, Money expenseAmount, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(period);

        if (!account.RoundUpsOn || account.RoundUpBucketId is not { } bucketId) return null;
        if (period.Status != PeriodStatus.Open) return null;

        var change = account.RoundUpFor(expenseAmount.Amount);
        if (change <= 0m) return null;

        // Don't sweep money that isn't there. Savings allocations are deliberately allowed to exceed available cash
        // (they're advisory earmarks), but a round-up is automatic: pushing "free to allocate" negative — and raising
        // the "Off balance — overspent" alarm — over 40 cents nobody chose to move would be the feature working
        // against the user. When there's no headroom the change simply isn't taken.
        var priorSaved = _savings.AccumulatedTotal(account) - period.SavingsNetTotal;
        var headroom = period.MaxAdditionalSavingsAfter(priorSaved);
        var sweep = new Money(change, account.Currency);
        if (sweep > headroom) return null;

        return period.AllocateToSavings(bucketId, sweep, date, SweepNote);
    }
}
