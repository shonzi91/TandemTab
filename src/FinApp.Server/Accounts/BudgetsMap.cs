using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin-Budgets read model (<see cref="BudgetsViewDto"/>): each budgeted category with its
/// coverage (allocated / spent / remaining), computed server-side via <see cref="BudgetCoverageService"/>.</summary>
public static class BudgetsMap
{
    public static BudgetsViewDto View(Account account, long version, Period? viewPeriod = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return BudgetsViewDto.Empty with { Version = version, Currency = account.Currency };

        var coverage = new BudgetCoverageService();
        var rows = period.Budgets.Select(b =>
        {
            var cov = coverage.ForCategory(account, period, b.CategoryId);
            var cat = account.FindCategory(b.CategoryId);
            return new BudgetRowDto(b.CategoryId, cat?.Name ?? "—", cat?.Icon,
                cov.Allocated.Amount, cov.Spent.Amount, cov.Remaining.Amount,
                b.AlertThreshold, b.NotifyOnEveryExpense, cov.IsOverBudget);
        }).ToList();

        var categories = account.Categories
            .Where(c => !c.IsArchived)
            .Select(c => new CategoryOptionDto(c.Id, c.Name, c.Icon, c.ParentId))
            .ToList();

        return new BudgetsViewDto(version, account.Currency,
            period.BudgetedTotal.Amount, period.ExpensesTotal.Amount, rows, categories);
    }
}
