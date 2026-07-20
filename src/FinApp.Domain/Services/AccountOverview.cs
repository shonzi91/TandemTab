using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;

namespace FinApp.Domain.Services;

/// <summary>
/// The Home balance-header figures, computed from the domain aggregate. This is the <b>first read moved
/// server-side</b> under the Option-A migration (docs/MOBILE.md): the server exposes it at
/// <c>GET /accounts/{id}/overview</c> and the web client renders the result instead of recomputing the money
/// model locally, so the numbers can't drift between web and (future) native. Pure — no I/O, moves no money.
///
/// <para>Mirrors what <c>BudgetingState</c> put in the balance header, using the same domain methods:
/// <c>Current</c> is the period's expected closing balance; <c>Free</c> is cash minus savings earmarks
/// (budgets are advisory, they don't reserve — see <see cref="Period.FreeToAllocateAfter"/>); <c>Saved</c>
/// is the earmarked remainder (<c>current − free</c>); <c>SafeAfterBills</c> subtracts the known recurring
/// bills still due this period and may be negative.</para>
///
/// <para>Bank-live-balance adjustment (the header's <c>DisplayClosingBalance</c>) is deliberately <b>not</b>
/// applied here — it's a display concern needing the live bank figure, and it stays client-side for this
/// first slice. On accounts with no synced fund the two are identical.</para>
/// </summary>
public readonly record struct AccountOverview(
    Money Current, Money Free, Money Saved, Money Spent, Money Contributed, Money BillsDue, Money SafeAfterBills)
{
    public static AccountOverview For(Account account, Period period)
    {
        // Prior-period savings reserved out of "free", same as BudgetingState.PriorSaved.
        var priorSaved = new SavingsReportService().AccumulatedTotal(account) - period.SavingsNetTotal;
        var current = period.ExpectedClosingBalance;
        var free = period.FreeToAllocateAfter(priorSaved);

        // Known bills still expected out this period — the same rule as BudgetingState.BillsDueThisPeriod:
        // active, known-amount recurring expenses not yet handled this period. Only while the period is open.
        var billsDue = period.Status == PeriodStatus.Open
            ? new Money(account.RecurringItems
                .Where(r => r.Kind == RecurringKind.Expense && r.HasKnownAmount && r.IsPending(period.From, period.To))
                .Sum(r => r.ExpectedAmount), account.Currency)
            : Money.Zero(account.Currency);

        return new AccountOverview(
            current, free, current - free,
            period.ExpensesTotal, period.ContributionsPaidTotal,
            billsDue, free - billsDue);
    }
}
