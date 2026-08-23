using System.Text.Json.Nodes;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Recurring;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Sub-categories are gone: each one becomes a tag bound to its old parent.
/// <para>
/// These tests go through the <b>snapshot</b> rather than the domain API on purpose — that is the only way the
/// legacy shape can exist any more (<c>AddCategory</c> refuses to nest), and it is exactly what production hits:
/// an account saved months ago, loaded by a server that no longer has the concept. Building the state some other
/// way would test a path no user can be on.
/// </para>
/// </summary>
public class CategoryFlattenTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>Re-load <paramref name="account"/> with <paramref name="childName"/> recorded as a sub-category of
    /// <paramref name="parentId"/> — the snapshot an older build would have written. Shared with the coverage and
    /// emergency-fund tests, which check the conversion doesn't move anyone's figures.</summary>
    internal static Account LoadAsLegacy(Account account, string childName, Guid parentId)
    {
        var node = JsonNode.Parse(AccountSnapshotSerializer.Serialize(account))!;
        foreach (var c in node["Categories"]!.AsArray())
            if (c!["Name"]!.GetValue<string>() == childName)
                c["ParentId"] = parentId.ToString();
        return AccountSnapshotSerializer.Deserialize(node.ToJsonString());
    }

    private static (Account Account, Guid Food, Guid Groceries, Guid Member, Guid Fund) Setup()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food", icon: "🍽️");
        var groceries = account.AddCategory("Groceries", icon: "🛒");
        var member = Guid.NewGuid();
        var fund = account.AddFund("Cash");
        return (account, food.Id, groceries.Id, member, fund.Id);
    }

    [Fact]
    public void A_sub_category_becomes_a_tag_bound_to_its_old_parent()
    {
        var (account, food, groceries, member, fund) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(groceries, M(40), new DateOnly(2026, 1, 5), member, fund));

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // The category list is flat and Groceries is no longer one of them.
        Assert.All(loaded.Categories, c => Assert.True(c.IsRoot));
        Assert.Single(loaded.Categories);
        Assert.Equal("Food", loaded.Categories[0].Name);

        // It is a tag now — carrying the sub-category's own id, its icon, and a binding back to Food so picking
        // "Groceries" on a new expense still files it the way nesting used to.
        var tag = Assert.Single(loaded.Tags);
        Assert.Equal("Groceries", tag.Name);
        Assert.Equal(groceries, tag.Id);
        Assert.Equal("🛒", tag.Icon);
        Assert.Equal(food, tag.CategoryId);
        Assert.False(tag.IsArchived);

        // The expense moved to the parent and kept the distinction as its tag — same money, still separable.
        var expense = Assert.Single(loaded.Periods[0].Expenses);
        Assert.Equal(food, expense.CategoryId);
        Assert.Equal(tag.Id, expense.TagId);
        Assert.Equal(M(40), expense.Amount);

        Assert.Equal(1, loaded.LastCategoryFlatten.CategoriesConverted);
        Assert.Equal(1, loaded.LastCategoryFlatten.ExpensesRefiled);
    }

    [Fact]
    public void An_expense_that_already_has_a_tag_keeps_it_and_is_counted()
    {
        var (account, food, groceries, member, fund) = Setup();
        var lidl = account.AddTag("Lidl");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(groceries, M(40), new DateOnly(2026, 1, 5), member, fund));
        expense.SetTag(lidl.Id);

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // One tag per expense is the model, so the tag the user chose for this row wins. The expense still re-files,
        // so no money moves — it just stops being separable as "Groceries".
        var moved = Assert.Single(loaded.Periods[0].Expenses);
        Assert.Equal(food, moved.CategoryId);
        Assert.Equal(lidl.Id, moved.TagId);
        Assert.Equal(0, loaded.LastCategoryFlatten.ExpensesRefiled);
        Assert.Equal(1, loaded.LastCategoryFlatten.ExpensesTagSlotTaken);
    }

    [Fact]
    public void Child_budgets_merge_into_the_parents_rather_than_being_dropped()
    {
        var (account, food, groceries, _, _) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food, M(200), alertThreshold: 0.5m);
        period.AddBudget(groceries, M(400));

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // Food 200 + Groceries 400 was always one plan of 600; dropping the child's cap would quietly free 400.
        var budget = Assert.Single(loaded.Periods[0].Budgets);
        Assert.Equal(food, budget.CategoryId);
        Assert.Equal(M(600), budget.Allocated);
        Assert.Equal(0.5m, budget.AlertThreshold);   // the parent's own setting survives
        Assert.Equal(1, loaded.LastCategoryFlatten.BudgetsMerged);
    }

    [Fact]
    public void A_child_budget_under_an_unbudgeted_parent_becomes_the_parents_budget()
    {
        var (account, food, groceries, _, _) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(groceries, M(400), alertThreshold: 0.9m);

        var loaded = LoadAsLegacy(account, "Groceries", food);

        var budget = Assert.Single(loaded.Periods[0].Budgets);
        Assert.Equal(food, budget.CategoryId);
        Assert.Equal(M(400), budget.Allocated);
        Assert.Equal(0.9m, budget.AlertThreshold);   // inherited: the parent had no setting of its own to keep
    }

    [Fact]
    public void Budgets_merge_in_every_period_including_closed_ones()
    {
        var (account, food, groceries, _, _) = Setup();
        var january = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        january.AddBudget(groceries, M(300));
        january.Close();
        var february = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        february.AddBudget(food, M(100));
        february.AddBudget(groceries, M(250));

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // History is migrated too: a closed month whose budget still pointed at a category that no longer exists
        // would render its coverage ring against nothing.
        Assert.Equal(M(300), Assert.Single(loaded.Periods[0].Budgets).Allocated);
        Assert.Equal(M(350), Assert.Single(loaded.Periods[1].Budgets).Allocated);
        Assert.Equal(2, loaded.LastCategoryFlatten.BudgetsMerged);
    }

    [Fact]
    public void An_existing_tag_of_the_same_name_is_adopted_rather_than_duplicated()
    {
        var (account, food, groceries, member, fund) = Setup();
        var existing = account.AddTag("Groceries", "🥕");
        account.SetTagArchived(existing.Id, true);
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(groceries, M(40), new DateOnly(2026, 1, 5), member, fund));

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // Two tags called "Groceries" would split every future breakdown and break the account's own name rule.
        var tag = Assert.Single(loaded.Tags);
        Assert.Equal(existing.Id, tag.Id);
        Assert.Equal("🥕", tag.Icon);                 // the tag the user already had keeps its own icon
        Assert.False(tag.IsArchived);                 // un-hidden: it is about to carry history
        Assert.Equal(food, tag.CategoryId);
        Assert.Equal(tag.Id, Assert.Single(loaded.Periods[0].Expenses).TagId);
    }

    [Fact]
    public void Bills_trips_and_tag_bindings_that_pointed_at_the_sub_category_follow_the_parent()
    {
        var (account, food, groceries, _, fund) = Setup();
        account.AddRecurring(new RecurringItem("Veg box", RecurringKind.Expense, RecurringAmountMode.Fixed,
            30m, 5, groceries, fund));
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7));
        account.SetTripCategory(trip.Id, groceries);
        var other = account.AddTag("Market");
        account.SetTagCategory(other.Id, groceries);

        var loaded = LoadAsLegacy(account, "Groceries", food);

        // None of these hold history, but each would file its NEXT expense into a category that no longer exists.
        Assert.Equal(food, loaded.RecurringItems[0].CategoryId);
        Assert.Equal(food, loaded.Trips[0].CategoryId);
        Assert.Equal(food, loaded.Tags.Single(t => t.Name == "Market").CategoryId);
        Assert.Equal(1, loaded.LastCategoryFlatten.RecurringMoved);
    }

    [Fact]
    public void Flattening_is_idempotent_so_it_can_run_on_every_load()
    {
        var (account, food, groceries, member, fund) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food, M(200));
        period.AddBudget(groceries, M(400));
        period.AddExpense(new Expense(groceries, M(40), new DateOnly(2026, 1, 5), member, fund));

        var once = LoadAsLegacy(account, "Groceries", food);
        // A second pass must not re-merge the budget into 1,000 or re-tag anything.
        var again = once.FlattenCategoryTree();

        Assert.True(again.DidNothing);
        Assert.Equal(M(600), Assert.Single(once.Periods[0].Budgets).Allocated);
        Assert.Single(once.Tags);

        // And the same is true of a full save/load cycle, which is how it will actually be re-run in production.
        var roundTripped = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(once));
        Assert.True(roundTripped.LastCategoryFlatten.DidNothing);
        Assert.Equal(M(600), Assert.Single(roundTripped.Periods[0].Budgets).Allocated);
        Assert.Single(roundTripped.Tags);
        Assert.Single(roundTripped.Categories);
    }

    [Fact]
    public void An_orphan_sub_category_is_promoted_rather_than_lost()
    {
        var (account, _, groceries, member, fund) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(groceries, M(40), new DateOnly(2026, 1, 5), member, fund));

        // A parent that isn't in the snapshot at all — corrupt, but it has real history behind it.
        var loaded = LoadAsLegacy(account, "Groceries", Guid.NewGuid());

        var kept = Assert.Single(loaded.Categories, c => c.Name == "Groceries");
        Assert.True(kept.IsRoot);
        Assert.Empty(loaded.Tags);
        Assert.Equal(kept.Id, Assert.Single(loaded.Periods[0].Expenses).CategoryId);
    }

    [Fact]
    public void An_account_that_never_had_sub_categories_is_untouched()
    {
        var (account, food, _, member, fund) = Setup();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(food, M(40), new DateOnly(2026, 1, 5), member, fund));

        var loaded = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.True(loaded.LastCategoryFlatten.DidNothing);
        Assert.Equal(2, loaded.Categories.Count);
        Assert.Empty(loaded.Tags);
        Assert.Null(Assert.Single(loaded.Periods[0].Expenses).TagId);
    }

    [Fact]
    public void Every_category_the_app_can_create_is_top_level()
    {
        // ⚠️ This used to pass a parent id to AddCategory and assert it was ignored. The parameter is gone — it
        // invited callers to nest and then silently didn't, which is how the phone's editor came to offer a parent
        // picker that changed nothing — so tolerating one is now a WIRE concern, pinned by
        // CategoryApiTests.A_parent_id_from_an_older_client_is_ignored_rather_than_rejected.
        var account = new Account("Personal", Eur);
        account.AddCategory("Food");
        account.AddCategory("Groceries");

        Assert.All(account.Categories, c => Assert.True(c.IsRoot));
        Assert.Equal(2, account.Categories.Count);
    }
}
