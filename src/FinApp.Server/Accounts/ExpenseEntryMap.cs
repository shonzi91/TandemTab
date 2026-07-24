using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Accounts;

/// <summary>Builds the faster-expense-entry history (<see cref="ExpenseEntryDto"/>) — the recent manual expenses the
/// thick add-expense modal derives its chips/suggestions from. Mirrors <c>BudgetingState.ManualExpensesNewestFirst</c>:
/// periods newest→oldest, and within a period last-added-first, auto-filed bank rows excluded.</summary>
public static class ExpenseEntryMap
{
    /// <summary>How many recent manual expenses to send. Bounds the payload while covering the chips (top few) and the
    /// merchant→category suggestion (a match against the list) — you rarely re-enter a merchant older than this.</summary>
    private const int MaxRecent = 100;

    public static ExpenseEntryDto View(Account account, long version)
    {
        var recent = Enumerable.Reverse(account.Periods.ToList())
            .SelectMany(p => Enumerable.Reverse(p.Expenses.ToList()))
            .Where(e => !e.AutoFiled)
            .Take(MaxRecent)
            .Select(e => new RecentExpenseDto(e.CategoryId, e.FundId, e.Amount.Amount, e.Note, e.Date))
            .ToList();

        return new ExpenseEntryDto(version, recent);
    }
}
