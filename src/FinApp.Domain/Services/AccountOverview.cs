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
///
/// <para><b>The four trailing figures exist for the native client (R2 parity).</b> The web's Home hero is a
/// four-part money summary — safe to spend, saved (with its rate), spent (with the transfer half broken out),
/// money in (with carry-over broken out) — and every one of those beyond the first came from
/// <c>BudgetingState</c>, i.e. from the domain the thin clients deliberately do not carry. Android could render
/// three raw balances and nothing else. Rather than let the native app grow its own money model to catch up —
/// which is precisely the drift this type was created to prevent — the same figures are computed here, once.</para>
/// </summary>
/// <param name="MoneyIn">Everything there was to work with this period: fresh income + free carry-in. The
/// savings-rate denominator; <c>MoneyIn − Contributed</c> is the carried-over half.</param>
/// <param name="TransfersOut">The account-to-account half of money out, on its own. <see cref="Spent"/> stays
/// expenses-only (budget bars and health must not count a transfer as spending), so a client that wants the
/// hero's "all money out" adds the two — and can name the transfer part rather than let it read as a blow-out.</param>
/// <param name="SavedThisPeriod">Set aside this period, disbursements excluded — deploying a save to the goal it
/// was for still counts as saved. Distinct from <see cref="Saved"/>, which is the standing earmarked balance.</param>
/// <param name="SavedRate"><see cref="SavedThisPeriod"/> as a fraction of <see cref="MoneyIn"/>, or null when
/// nothing came in. Sent computed rather than left to each client: two clients dividing the same two numbers is
/// two chances to disagree about the zero case.</param>
public readonly record struct AccountOverview(
    Money Current, Money Free, Money Saved, Money Spent, Money Contributed, Money BillsDue, Money SafeAfterBills,
    Money MoneyIn, Money TransfersOut, Money SavedThisPeriod, decimal? SavedRate)
{
    public static AccountOverview For(Account account, Period period)
    {
        var savings = new SavingsReportService();
        // Prior-period savings reserved out of "free", same as BudgetingState.PriorSaved.
        var priorSaved = savings.AccumulatedTotal(account) - period.SavingsNetTotal;
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
            billsDue, free - billsDue,
            savings.MoneyIn(account, period), period.AccountTransfersOutTotal,
            period.SavingsSetAsideTotal, savings.PeriodMoneyInRate(account, period));
    }
}
