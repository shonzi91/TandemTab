namespace FinApp.Contracts;

// --- Path-B thin-client slice: Budgets (docs/MOBILE.md) ------------------------------------------------------
// Per-category spending plans with their coverage (allocated / spent / remaining), computed server-side.

/// <summary>One budgeted category with its coverage. <see cref="Remaining"/> = allocated − spent (may be negative);
/// <see cref="Over"/> is true past the allocation. <see cref="AlertThreshold"/> is the 0..1 spend level that warns.
/// <see cref="Essential"/> is the category's essential/discretionary flag (a discretionary row with money left is a
/// reallocation source — the client's "discretionary leftovers" list). <see cref="MaxBudget"/> is the most this
/// category's budget can be raised to (Current − savings + its own spend, minus the other budgets) — the edit cap,
/// computed server-side against the same prior-saved reserve the domain uses.</summary>
public record BudgetRowDto(
    Guid CategoryId,
    string Name,
    string? Icon,
    decimal Allocated,
    decimal Spent,
    decimal Remaining,
    decimal AlertThreshold,
    bool NotifyEvery,
    bool Over,
    bool Essential = false,
    decimal MaxBudget = 0m);

/// <summary>The Budgets surface in one read: the period totals, every budgeted category with coverage, and the
/// category options for adding a budget.</summary>
public record BudgetsViewDto(
    long Version,
    string Currency,
    decimal TotalBudgeted,
    decimal TotalSpent,
    IReadOnlyList<BudgetRowDto> Budgets,
    IReadOnlyList<CategoryOptionDto> Categories)
{
    public static readonly BudgetsViewDto Empty = new(0, "", 0m, 0m, [], []);
}

/// <summary>The delta a budget mutation returns (superset of <see cref="MutationResultDto"/>): new version, the
/// affected category id, and the refreshed view the client reconciles from.</summary>
public record BudgetMutationDto(long Version, Guid? EntityId, BudgetsViewDto View);
