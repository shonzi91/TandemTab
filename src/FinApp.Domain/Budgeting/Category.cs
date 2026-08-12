using FinApp.Domain.Common;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// A budget/expense category (Food, Bills, Car...). Stored flat on the <c>Account</c> and reused across periods.
/// <para>
/// Categories are a <b>flat list</b>. Sub-categories were removed: they were a second way to say what a tag
/// already says, and the budget tree — the screen people open daily — paid for it. Every sub-category converts
/// into a tag bound to its old parent (<c>Account.FlattenCategoryTree</c>), which keeps both the history and the
/// entry-time shortcut while leaving one level of budgets to read.
/// </para>
/// </summary>
public sealed class Category : Entity
{
    public string Name { get; private set; }

    /// <summary>Vestigial: sub-categories were removed. Retained only so older persisted snapshots keep
    /// deserializing — they are flattened on load, so nothing created now ever carries one. Same treatment as
    /// <c>Fund.ParentId</c>, which went the same way earlier.</summary>
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

    /// <summary>Promote a legacy sub-category to the top level. Used by the flatten when the recorded parent is
    /// missing from the snapshot: an orphan is kept as a category of its own rather than converted into a tag
    /// pointing at nothing (and never silently dropped — it has history behind it).</summary>
    internal void ClearParent() => ParentId = null;

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

/// <summary>
/// What <c>Account.FlattenCategoryTree</c> actually did. Returned rather than logged so the caller can say it out
/// loud: a conversion that silently rewrites how a year of spending is filed is the kind of thing a user should be
/// told about once, not discover.
/// </summary>
/// <param name="CategoriesConverted">Sub-categories that became tags.</param>
/// <param name="ExpensesRefiled">Expenses moved to the parent AND labelled with the new tag.</param>
/// <param name="ExpensesTagSlotTaken">Expenses moved to the parent but left with the tag they already had — one tag
/// per expense, and the one already there was chosen deliberately. These lose the sub-category distinction.</param>
/// <param name="BudgetsMerged">Child budgets folded into a parent's, across every period.</param>
/// <param name="RecurringMoved">Bills/incomes re-pointed at the parent category.</param>
public sealed record CategoryFlattenResult(
    int CategoriesConverted, int ExpensesRefiled, int ExpensesTagSlotTaken, int BudgetsMerged, int RecurringMoved)
{
    /// <summary>The result of a flatten that found nothing to do — every account, on every load, after the first.</summary>
    public static readonly CategoryFlattenResult Nothing = new(0, 0, 0, 0, 0);

    /// <summary>True when the tree was already flat, so a caller can skip persisting or reporting anything.</summary>
    public bool DidNothing => CategoriesConverted == 0 && ExpensesRefiled == 0 && ExpensesTagSlotTaken == 0
                              && BudgetsMerged == 0 && RecurringMoved == 0;
}
