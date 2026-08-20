using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Services;

/// <summary>How much of a budget is used, for charts and threshold alerts (feature 6).</summary>
public sealed record BudgetCoverage(Money Allocated, Money Spent, decimal AlertThreshold)
{
    public Money Remaining => Allocated - Spent;
    public bool IsOverBudget => Spent > Allocated;

    /// <summary>Fraction spent (0..1+). Null when nothing is allocated (avoids divide-by-zero).</summary>
    public decimal? Ratio => Spent.RatioOf(Allocated);

    /// <summary>Percent spent rounded to whole numbers, or null when nothing is allocated.</summary>
    public int? Percent => Ratio is { } r ? (int)decimal.Round(r * 100m, 0, MidpointRounding.AwayFromZero) : null;

    /// <summary>True once spending reaches the configured alert threshold (or any overspend on a zero budget).</summary>
    public bool ThresholdReached => Ratio is { } r ? r >= AlertThreshold : IsOverBudget;
}

/// <summary>
/// Computes budget usage by rolling up every expense in a category and its sub-categories
/// against the period's allocation for that category.
/// </summary>
public sealed class BudgetCoverageService
{
    public BudgetCoverage ForCategory(Account account, Period period, Guid categoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(period);

        var budget = period.FindBudget(categoryId)
            ?? throw new InvalidOperationException("No budget exists for this category in the period.");

        if (account.FindCategory(categoryId) is null)
            throw new InvalidOperationException("Category not found in the account.");

        var categoryIds = account.CategoryWithDescendantIds(categoryId).ToHashSet();

        var spent = SpentIn(period, categoryIds);

        return new BudgetCoverage(budget.Allocated, spent, budget.AlertThreshold);
    }

    /// <summary>
    /// What has gone out against a set of categories this period: expenses, plus any money sent to another account
    /// that the user filed under one of them.
    ///
    /// <para><b>★ Transfers count only when they are categorised</b> (S111, owner ask). This money was already in
    /// every "money out" total the app shows — <c>Period.AccountTransfersOutTotal</c> is added to the Home "Spent"
    /// tile and the Breakdown gives it a slice — but it reached no budget, because a budget is a per-category cap
    /// and a transfer had no category. So a standing €400 to the household account sat inside "Spent" while
    /// belonging to no plan. An uncategorised transfer still behaves exactly as it did.</para>
    ///
    /// <para>⚠️ Every figure that answers "what went on this category" has to use this, or one screen will hold
    /// two answers. <c>BudgetingState.SpentInCategory</c> is the other caller.</para>
    /// </summary>
    public static Money SpentIn(Period period, IReadOnlySet<Guid> categoryIds)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(categoryIds);
        var expenses = period.Expenses
            .Where(e => categoryIds.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money.Zero(period.Currency), (acc, m) => acc + m);
        var sentOut = period.AccountTransfersOut
            .Where(t => t.CategoryId is { } c && categoryIds.Contains(c))
            .Select(t => t.Amount)
            .Aggregate(Money.Zero(period.Currency), (acc, m) => acc + m);
        return expenses + sentOut;
    }
}
