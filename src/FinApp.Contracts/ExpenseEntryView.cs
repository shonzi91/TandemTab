namespace FinApp.Contracts;

// --- Path-B thin-client slice: faster-expense-entry helpers (docs/DOMAIN-REMOVAL.md) -------------------------
// The add-expense modal's conveniences — "repeat last", recent-merchant chips, recently-used categories, the
// last fund used for a category, and the imported-row category guess — are all derived in the thick client from
// its manual-expense history (BudgetingState.ManualExpensesNewestFirst). That history lives across every period in
// the aggregate. Rather than ship several precomputed lists, the server hands over the recent manual expenses in
// one compact, newest-first list and the client derives each helper from it (pure list arithmetic, no money model),
// so RecentMerchants / RecentCategories / LastFundForCategory / LastExpense / SuggestExpenseCategory stay identical.

/// <summary>One past manual expense, trimmed to what the entry helpers need. Bank-auto-filed rows are excluded
/// server-side (they reflect the bank, not a deliberate choice). Newest-first in <see cref="ExpenseEntryDto.Recent"/>,
/// and within a period last-added-first — the same ordering the thick client walks.</summary>
public record RecentExpenseDto(Guid CategoryId, Guid FundId, decimal Amount, string? Note, DateOnly Date);

/// <summary>The faster-expense-entry history: the most recent manual expenses (capped), from which the client derives
/// "repeat last", recent-merchant chips, recently-used categories, the per-category last fund, and the
/// merchant→category suggestion for imported rows.</summary>
public record ExpenseEntryDto(long Version, IReadOnlyList<RecentExpenseDto> Recent)
{
    public static readonly ExpenseEntryDto Empty = new(0, []);
}
