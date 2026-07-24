namespace FinApp.Contracts;

// --- Path-B thin Income surface (docs/MOBILE.md) ------------------------------------------------------------
// The thin counterpart of the thick app's "Add income". Deposits (Contributions) merge server-side by
// (member, category, fund), so a row here is a merged total; adding to an existing combo raises that row.
// Reuses CategoryOptionDto / FundOptionDto (SpendingView.cs) for the pickers — the category picker offers
// *contribution* categories (income sources), not spend categories.

/// <summary>One (merged) income row with display fields resolved server-side. <see cref="CategoryId"/> is
/// <see cref="System.Guid.Empty"/> for general (uncategorised) income.</summary>
public record DepositRowDto(
    Guid Id,
    string MemberName,
    Guid CategoryId,
    string CategoryName,
    string? CategoryIcon,
    Guid FundId,
    string FundName,
    decimal Amount,
    DateOnly Date);

/// <summary>The thin Income surface: this period's deposits + the pickers + the overview (for Contributed).</summary>
public record IncomeViewDto(
    long Version,
    string Currency,
    AccountOverviewDto Overview,
    IReadOnlyList<DepositRowDto> Deposits,
    IReadOnlyList<CategoryOptionDto> Categories,
    IReadOnlyList<FundOptionDto> Funds)
{
    public static readonly IncomeViewDto Empty = new(0, "", AccountOverviewDto.Empty, [], [], []);
}
