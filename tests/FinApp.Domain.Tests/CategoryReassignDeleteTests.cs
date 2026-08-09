using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Deleting a category that history references. The plain <c>RemoveCategory</c> refuses; this path deletes it and
/// re-files what it held, so the promise under test is that no money and no row is lost — only the label changes.
/// </summary>
public class CategoryReassignDeleteTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Deleting_moves_its_expenses_and_keeps_every_field_including_the_id()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var transport = account.AddCategory("Transport");
        var member = Guid.NewGuid();
        var fund = Guid.NewGuid();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(42.50m), new DateOnly(2026, 1, 5), member, fund, "Lunch"));
        var originalId = expense.Id;

        account.RemoveCategoryReassigning(food.Id, transport.Id);

        Assert.Null(account.FindCategory(food.Id));
        var moved = Assert.Single(period.Expenses);
        Assert.Equal(originalId, moved.Id);              // the row is re-filed, not replaced
        Assert.Equal(transport.Id, moved.CategoryId);
        Assert.Equal(M(42.50m), moved.Amount);
        Assert.Equal(new DateOnly(2026, 1, 5), moved.Date);
        Assert.Equal(member, moved.MemberId);
        Assert.Equal(fund, moved.FundId);
        Assert.Equal("Lunch", moved.Note);
    }

    [Fact]
    public void Sub_categories_go_with_it_and_their_expenses_land_on_the_target_too()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var groceries = account.AddCategory("Groceries", food.Id);
        var transport = account.AddCategory("Transport");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(food.Id, M(10), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid()));
        period.AddExpense(new Expense(groceries.Id, M(15), new DateOnly(2026, 1, 6), Guid.NewGuid(), Guid.NewGuid()));

        account.RemoveCategoryReassigning(food.Id, transport.Id);

        Assert.Null(account.FindCategory(food.Id));
        Assert.Null(account.FindCategory(groceries.Id));
        Assert.All(period.Expenses, e => Assert.Equal(transport.Id, e.CategoryId));
        Assert.Equal(25m, period.Expenses.Sum(e => e.Amount.Amount));   // the money is untouched
    }

    [Fact]
    public void Expenses_in_closed_periods_move_too()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var transport = account.AddCategory("Transport");
        var january = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        january.AddExpense(new Expense(food.Id, M(20), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid()));
        january.Close();
        var february = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        february.AddExpense(new Expense(food.Id, M(30), new DateOnly(2026, 2, 5), Guid.NewGuid(), Guid.NewGuid()));

        account.RemoveCategoryReassigning(food.Id, transport.Id);

        Assert.Equal(transport.Id, january.Expenses.Single().CategoryId);
        Assert.Equal(transport.Id, february.Expenses.Single().CategoryId);
    }

    /// <summary>A cap is a decision the user made about a category; it must not be inherited by the target.</summary>
    [Fact]
    public void Its_budget_is_dropped_and_the_targets_budget_is_left_alone()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var transport = account.AddCategory("Transport");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(200));
        period.AddBudget(transport.Id, M(50));

        account.RemoveCategoryReassigning(food.Id, transport.Id);

        Assert.Null(period.FindBudget(food.Id));
        Assert.Equal(M(50), period.FindBudget(transport.Id)!.Allocated);   // not 250
    }

    [Fact]
    public void A_tag_bound_to_the_deleted_category_is_unbound_rather_than_left_dangling()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var transport = account.AddCategory("Transport");
        var tag = account.AddTag("Takeaway");
        account.SetTagCategory(tag.Id, food.Id);

        account.RemoveCategoryReassigning(food.Id, transport.Id);

        Assert.Null(account.FindTag(tag.Id)!.CategoryId);
    }

    [Fact]
    public void The_target_cannot_be_the_category_being_deleted_or_one_of_its_subs()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var groceries = account.AddCategory("Groceries", food.Id);

        Assert.Throws<InvalidOperationException>(() => account.RemoveCategoryReassigning(food.Id, food.Id));
        // A sub-category is deleted along with its parent, so re-filing into it would strand the expenses.
        Assert.Throws<InvalidOperationException>(() => account.RemoveCategoryReassigning(food.Id, groceries.Id));
        Assert.NotNull(account.FindCategory(food.Id));   // nothing was removed on the way to the throw
    }
}
