using FinApp.Domain.Common;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// A budget/expense category (Food, Bills, Car...). Categories form a tree via <see cref="ParentId"/>,
/// but are stored flat on the <c>Account</c> (which owns tree navigation). Flat storage round-trips
/// cleanly through the relational store. Categories are reused across periods.
/// </summary>
public sealed class Category : Entity
{
    public string Name { get; private set; }
    public Guid? ParentId { get; private set; }

    /// <summary>
    /// An optional display icon (emoji) for the category. Null means "no explicit choice" — the UI then
    /// derives one from the name. Body data: travels in the account snapshot, not the relational header.
    /// </summary>
    public string? Icon { get; private set; }

    /// <summary>
    /// Whether this is an <b>essential</b> spend (rent, groceries, health...) as opposed to discretionary.
    /// Purely advisory: it never affects budgets or balances — it just lets the app avoid ever suggesting you
    /// redirect essential money (e.g. toward a debt). Body data: travels in the snapshot, not the relational header.
    /// </summary>
    public bool IsEssential { get; private set; }

    /// <summary>Archived categories are hidden from the pickers and budget/expense lists but keep all their history —
    /// past expenses and budgets still resolve their name. A category is archived (not hard-deleted) so referencing
    /// expenses are never orphaned. Body data: travels in the snapshot, not the relational header.</summary>
    public bool IsArchived { get; private set; }

    public Category(string name, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.", nameof(name));
        Name = name.Trim();
        ParentId = parentId;
    }

    public bool IsRoot => ParentId is null;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.", nameof(name));
        Name = name.Trim();
    }

    /// <summary>Set (or clear, with null/empty) the category's display icon.</summary>
    public void SetIcon(string? icon) => Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    /// <summary>Mark this category as an essential (or discretionary) spend.</summary>
    public void SetEssential(bool essential) => IsEssential = essential;

    /// <summary>Hide/show this category in the pickers and lists (its history is kept regardless).</summary>
    public void SetArchived(bool archived) => IsArchived = archived;
}
