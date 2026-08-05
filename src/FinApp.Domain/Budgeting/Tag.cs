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

    /// <summary>
    /// Optional category this tag files into (F2): applying the tag on a new expense pre-selects this category, so
    /// categorizing stops being a separate manual step for the labels a user already applies (tag <c>lidl</c> → Food).
    /// Null means the tag carries no filing opinion, which is what every tag is until deliberately bound.
    /// <para>
    /// A <b>default at entry time</b>, never a rule applied to stored rows: past expenses keep the category they were
    /// filed under, so binding a tag can't silently re-write history or move money between budgets after the fact.
    /// </para>
    /// Body data — travels in the account snapshot, not the relational header.
    /// </summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Bind this tag to a category (or clear the binding with null / <see cref="Guid.Empty"/>).
    /// Validated by <c>Account.SetTagCategory</c>, which owns the category tree.</summary>
    public void SetCategory(Guid? categoryId) =>
        CategoryId = categoryId is { } id && id != Guid.Empty ? id : null;

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
