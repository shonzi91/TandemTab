using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin-Structure read model (<see cref="StructureViewDto"/>): the account's spend
/// categories, funds and contribution (income) categories — with their icons, hierarchy (<c>ParentId</c>) and
/// archived/essential/synced flags — so the thin editor can list them. Account-level (not period-scoped). The
/// matching create/edit/archive/remove command endpoints already exist (Session 44).</summary>
public static class StructureMap
{
    public static StructureViewDto View(Account account, long version) =>
        new(version,
            account.Categories
                .Select(c => new StructureCategoryDto(c.Id, c.Name, c.Icon, c.ParentId, c.IsEssential, c.IsArchived))
                .ToList(),
            account.Funds
                .Select(f => new StructureFundDto(f.Id, f.Name, f.Icon, f.Note, f.ParentId, f.IsSynced, f.IsArchived))
                .ToList(),
            account.ContributionCategories
                .Select(cc => new StructureContributionCategoryDto(cc.Id, cc.Name, cc.Icon))
                .ToList());
}
