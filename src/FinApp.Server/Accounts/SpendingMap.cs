using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the Path-B thin-Spending read-model DTOs from the domain aggregate (see <see cref="SpendingViewDto"/>).
/// Resolving the display fields (category name/icon, fund name) and the overview here — server-side, from the one
/// domain — is the whole point of the slice: the client renders spending without carrying the money model.
/// </summary>
public static class SpendingMap
{
    /// <summary>The balance-header figures for a period, as the wire DTO.</summary>
    public static AccountOverviewDto Overview(Account account, Period period)
    {
        var ov = AccountOverview.For(account, period);
        return new AccountOverviewDto(
            account.Currency, ov.Current.Amount, ov.Free.Amount, ov.Saved.Amount,
            ov.Spent.Amount, ov.Contributed.Amount, ov.BillsDue.Amount, ov.SafeAfterBills.Amount);
    }

    /// <summary>One expense row with its display fields resolved.</summary>
    public static ExpenseDto ToDto(Account account, Expense e) => new(
        e.Id,
        e.CategoryId,
        account.FindCategory(e.CategoryId)?.Name ?? "—",
        account.FindCategory(e.CategoryId)?.Icon,
        e.FundId,
        account.FundName(e.FundId),
        e.Amount.Amount,
        e.Date,
        e.Note,
        e.AutoFiled,
        e.IsFromSavings,
        e.OnBehalfOfOtherAccount,
        e.IsSettlementSource,
        e.IsSettlementDestination);

    /// <summary>The full Spending surface for the account's current period (empty currency-only view when none).</summary>
    public static SpendingViewDto View(Account account, long version)
    {
        if (account.CurrentPeriod is not { } period)
            return SpendingViewDto.Empty with { Version = version, Currency = account.Currency };

        var expenses = period.Expenses
            .OrderByDescending(e => e.Date)
            .Select(e => ToDto(account, e))
            .ToList();

        // Pickers show active categories/funds; a manual expense can't target a synced (bank) fund, but we send the
        // flag rather than filtering so the client can label/disable it — the same choice the current UI makes.
        var categories = account.Categories
            .Where(c => !c.IsArchived)
            .Select(c => new CategoryOptionDto(c.Id, c.Name, c.Icon, c.ParentId))
            .ToList();
        var funds = account.RootFunds
            .Where(f => !f.IsArchived)
            .Select(f => new FundOptionDto(f.Id, f.Name, f.IsSynced))
            .ToList();

        return new SpendingViewDto(version, account.Currency, Overview(account, period), expenses, categories, funds);
    }
}
