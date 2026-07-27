using FinApp.Domain.Common;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// A cross-cutting label attached to expenses (e.g. "Vacation", "Work", "Reimbursable"). Unlike a
/// <see cref="Category"/> a tag is flat (no tree) and an expense can carry several — so tags cut across
/// categories for analysis, sitting <i>alongside</i> sub-categories rather than replacing them. Stored flat
/// on the <c>Account</c>; travels in the account snapshot, not the relational header.
/// </summary>
public sealed class Tag : Entity
{
    public string Name { get; private set; }

    /// <summary>Optional display icon (emoji). Null means "no explicit choice" — the UI derives one from the name.</summary>
    public string? Icon { get; private set; }

    /// <summary>Archived tags are hidden from the pickers but keep their history — past expenses still resolve the name.
    /// A tag is archived (not hard-deleted) when it's still referenced, so tagged expenses are never orphaned.</summary>
    public bool IsArchived { get; private set; }

    public Tag(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name is required.", nameof(name));
        Name = name.Trim();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name is required.", nameof(name));
        Name = name.Trim();
    }

    /// <summary>Set (or clear, with null/empty) the tag's display icon.</summary>
    public void SetIcon(string? icon) => Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    /// <summary>Hide/show this tag in the pickers (its history is kept regardless).</summary>
    public void SetArchived(bool archived) => IsArchived = archived;
}
