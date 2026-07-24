namespace FinApp.Contracts;

// --- Path-B thin Structure surface (docs/MOBILE.md) ----------------------------------------------------------
// The thin counterpart of the thick app's account-structure editor: spend categories, funds, and contribution
// (income) categories, for create/edit/archive/remove. Account-level (not period-scoped). All the writes already
// exist as command endpoints (Session 44); this read model is what the thin editor lists them from. Every row
// carries ParentId so the client can render the (one-level) hierarchy; archived rows are included so the editor
// can offer restore. Removal blockers (a category/fund still referenced by a row) are enforced server-side on
// DELETE (400) rather than pre-computed here.

/// <summary>One spend category. <see cref="ParentId"/> is null for a top-level category.</summary>
public record StructureCategoryDto(Guid Id, string Name, string? Icon, Guid? ParentId, bool Essential, bool Archived);

/// <summary>One fund (wallet). <see cref="Synced"/> marks a bank-linked fund; <see cref="ParentId"/> is null at the top level.</summary>
public record StructureFundDto(Guid Id, string Name, string? Icon, string? Note, Guid? ParentId, bool Synced, bool Archived);

/// <summary>One contribution (income) category. These have no archived/parent state in the domain.</summary>
public record StructureContributionCategoryDto(Guid Id, string Name, string? Icon);

/// <summary>The thin Structure surface: the account's editable categories, funds and contribution categories.</summary>
public record StructureViewDto(
    long Version,
    IReadOnlyList<StructureCategoryDto> Categories,
    IReadOnlyList<StructureFundDto> Funds,
    IReadOnlyList<StructureContributionCategoryDto> ContributionCategories)
{
    public static readonly StructureViewDto Empty = new(0, [], [], []);
}
