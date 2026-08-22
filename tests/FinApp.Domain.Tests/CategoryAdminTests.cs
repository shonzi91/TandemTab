using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

public class CategoryAdminTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Category_icon_defaults_to_null_and_can_be_set_and_cleared()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food", icon: "🍽️");
        Assert.Equal("🍽️", food.Icon);

        var other = account.AddCategory("Other");
        Assert.Null(other.Icon);

        account.SetCategoryIcon(other.Id, "🎁");
        Assert.Equal("🎁", other.Icon);

        account.SetCategoryIcon(other.Id, "  ");   // blank clears it
        Assert.Null(other.Icon);
    }

    [Fact]
    public void Category_with_a_budget_cannot_be_removed()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(100));

        Assert.Equal("a budget references it", account.CategoryRemovalBlocker(food.Id));
        Assert.Throws<InvalidOperationException>(() => account.RemoveCategory(food.Id));
    }

    [Fact]
    public void Categories_are_flat_so_nothing_can_be_created_under_another()
    {
        // Sub-categories were removed — see CategoryFlattenTests for what happens to the ones already out there.
        // ⚠️ AddCategory no longer takes a parent id at all: it used to, and dropped it, which reads as an offer.
        // Tolerating one from an older client is a wire concern now — see CategoryApiTests.
        var account = new Account("Personal", Eur);
        var kids = account.AddCategory("Kids");

        var kid1 = account.AddCategory("Kid1");

        Assert.True(kid1.IsRoot);
        Assert.DoesNotContain(account.Categories, c => !c.IsRoot);
        Assert.Null(account.CategoryRemovalBlocker(kids.Id));
    }

    [Fact]
    public void Unused_category_is_removed()
    {
        var account = new Account("Personal", Eur);
        var spare = account.AddCategory("Spare");
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Null(account.CategoryRemovalBlocker(spare.Id));
        account.RemoveCategory(spare.Id);
        Assert.Empty(account.Categories);
    }

    [Fact]
    public void Budget_can_be_removed_from_a_period()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(100));

        period.RemoveBudget(food.Id);
        Assert.Null(period.FindBudget(food.Id));
    }
}
