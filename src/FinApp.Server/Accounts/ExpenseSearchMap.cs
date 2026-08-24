using System.Globalization;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Accounts;

/// <summary>
/// Search every period's expenses — the pool a thin client's "find an older expense" pickers draw from.
///
/// <para>★ <b>Why the server does this.</b> Every other spending read is period-scoped, so a phone holds one month
/// and cannot answer "which dinner did this €20 come back on" when the dinner was in June. The alternative — pull
/// each period's <c>/spending</c> and search locally — is the aggregation-in-a-second-language mistake the Trends
/// row already made the case against.</para>
///
/// <para>⚠️ The matching rules here MUST agree with <c>BudgetingState.RecentExpensesAcrossPeriods</c>, which is
/// what the web's pickers use. Two clients that search the same words and return different rows is a bug nobody
/// reports, because each looks correct on its own.</para>
/// </summary>
public static class ExpenseSearchMap
{
    /// <param name="q">Free text over the note, the category name <b>and the amount</b>. Blank returns the most
    /// recent <paramref name="take"/>; a term is matched across <b>every</b> period before the cap is applied, so
    /// an older row is reachable by typing rather than by paging.</param>
    /// <param name="refundableOnly">Keep only rows that still carry money — you cannot get money back off an
    /// expense that has already been refunded to nothing. Deliberately NOT filtered to rows big enough to absorb
    /// the credit: the domain refuses an over-refund with the figure in its message, which tells the user which
    /// row they picked wrongly, where hiding the candidate would leave them hunting for one they remember.</param>
    public static ExpenseSearchDto View(Account account, string? q = null, int take = 60, bool refundableOnly = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        var term = q?.Trim();
        var rows = account.Periods.SelectMany(p => p.Expenses)
            .Where(e => !refundableOnly || e.Amount.Amount > 0m)
            .Where(e => Matches(account, e, term))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(e => SpendingMap.ToDto(account, e))
            .ToList();

        return new ExpenseSearchDto(account.Currency, account.Periods.Sum(p => p.Expenses.Count), rows);
    }

    private static bool Matches(Account account, Expense e, string? term)
    {
        if (string.IsNullOrEmpty(term)) return true;
        var category = account.FindCategory(e.CategoryId)?.Name ?? "";
        return (e.Note ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || category.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || MatchesAmount(e.Amount, term);
    }

    /// <summary>Does a typed term look like this amount? Matched on the digits rather than parsed, so a half-typed
    /// "46.8" still finds €46.80 while the user is mid-keystroke. A comma is accepted for the decimal point because
    /// half of Europe types it that way, and the row's own currency symbol is never part of the match.
    /// <para>⚠️ A character-for-character port of <c>BudgetingState.MatchesAmount</c> — see the class remarks.</para></summary>
    private static bool MatchesAmount(Money amount, string term)
    {
        var wanted = term.Replace(',', '.').TrimStart('€', '$', '£').Trim();
        if (wanted.Length == 0 || !wanted.All(c => char.IsDigit(c) || c == '.')) return false;
        var exact = amount.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var trimmed = amount.Amount.ToString("0.##", CultureInfo.InvariantCulture);
        return exact.Contains(wanted, StringComparison.Ordinal) || trimmed.Contains(wanted, StringComparison.Ordinal);
    }
}
