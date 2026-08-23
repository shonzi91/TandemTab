using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin-Income read model (<see cref="IncomeViewDto"/>): this period's deposits
/// (contributions) with their display fields resolved server-side, plus the contribution-category + fund pickers
/// and the (bank-adjusted) overview. The automatic "from previous period" carryover pseudo-deposit is excluded —
/// it isn't user income. Mirrors what <c>BudgetingState</c> shows for the income surface.</summary>
public static class IncomeMap
{
    public static IncomeViewDto View(Account account, long version, decimal? bankBalance = null, string? bankCurrency = null, Period? viewPeriod = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return IncomeViewDto.Empty with { Version = version, Currency = account.Currency };

        var deposits = period.Contributions
            .Where(c => c.MemberId != Period.CarryoverSource)
            .OrderByDescending(c => c.Date)
            .Select(c => new DepositRowDto(
                c.Id,
                account.Members.FirstOrDefault(m => m.UserId == c.MemberId)?.DisplayName ?? "—",
                c.CategoryId,
                c.CategoryId == Guid.Empty ? "General income" : account.FindContributionCategory(c.CategoryId)?.Name ?? "—",
                c.CategoryId == Guid.Empty ? null : account.FindContributionCategory(c.CategoryId)?.Icon,
                c.FundId,
                account.FundName(c.FundId),
                c.Paid.Amount,
                c.Date))
            .ToList();

        var categories = account.ContributionCategories
            .Select(cc => new CategoryOptionDto(cc.Id, cc.Name, cc.Icon, null))
            .ToList();
        var funds = account.RootFunds
            .Where(f => !f.IsArchived)
            .Select(f => new FundOptionDto(f.Id, f.Name, f.IsSynced, f.Currency, f.Rate))
            .ToList();

        return new IncomeViewDto(version, account.Currency,
            SpendingMap.Overview(account, period, bankBalance, bankCurrency), deposits, categories, funds);
    }
}
