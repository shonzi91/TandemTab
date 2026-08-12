using FinApp.Domain.Common;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// A cross-cutting label attached to expenses (e.g. "Vacation", "Work", "Reimbursable"). A tag cuts across
/// categories, which is what makes it the app's one splitting axis: tags are now what sub-categories used to
/// be <i>and</i> what they never could be — "Lidl" can sit under Food this week and Household the next, and
/// still add up. Stored flat on the <c>Account</c>; travels in the account snapshot, not the relational header.
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

    /// <summary>
    /// This tag is one of the trip labels (Stay, Travel, Food &amp; drink, Tickets &amp; tours…) — the axis a
    /// <see cref="Trip"/>'s cost split is drawn on. Flagged so the everyday tag picker can leave them out:
    /// "Tickets &amp; tours" is noise when you're logging groceries, and a picker that grows six entries nobody
    /// uses at home is how tagging stops being used at all.
    /// <para>
    /// Nothing else treats them specially — a trip tag is an ordinary tag that can be renamed, re-iconed, bound to
    /// a category and archived like any other. The flag is a display hint, not a type.
    /// </para>
    /// </summary>
    public bool IsTripTag { get; private set; }

    /// <summary>Mark (or unmark) this tag as one of the trip labels.</summary>
    public void SetTripTag(bool isTripTag) => IsTripTag = isTripTag;

    public Tag(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name is required.", nameof(name));
        Name = name.Trim();
    }

    /// <summary>
    /// A tag carrying a chosen id — used only when a sub-category is converted into the tag that replaces it, so
    /// the label <i>inherits the sub-category's identity</i> rather than being a new thing with the same name.
    /// <para>
    /// That identity is what makes the conversion safe to run on every load: the flatten mints the same id every
    /// time, so a client that read a snapshot and posted back a tag id is never talking about a tag the server
    /// has since re-created under a different id. Ids are otherwise generated, never chosen — hence internal.
    /// </para>
    /// </summary>
    internal Tag(string name, Guid id) : this(name) => Id = id;

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
