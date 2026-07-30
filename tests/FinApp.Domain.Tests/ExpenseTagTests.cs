using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using Xunit;

namespace FinApp.Domain.Tests;

public class ExpenseTagTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void SetTags_dedups_and_drops_empty_ids()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var expense = new Expense(Guid.NewGuid(), M(10), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid());

        expense.SetTags([a, b, a, Guid.Empty]);

        Assert.Equal([a, b], expense.TagIds);
    }

    [Fact]
    public void SetTags_with_empty_clears_all()
    {
        var expense = new Expense(Guid.NewGuid(), M(10), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid());
        expense.SetTags([Guid.NewGuid()]);
        Assert.NotEmpty(expense.TagIds);

        expense.SetTags([]);
        Assert.Empty(expense.TagIds);
    }

    [Fact]
    public void Editing_an_expense_carries_its_tags_onto_the_new_entry()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();
        var trip = account.AddTag("Trip");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(20), new DateOnly(2026, 1, 5), member, fund));
        expense.SetTags([trip.Id]);

        // Edit mints a NEW id (append-only) — the tags must ride along.
        var edited = period.EditExpense(expense.Id, food.Id, M(25), fund, "corrected", new DateOnly(2026, 1, 6));

        Assert.NotEqual(expense.Id, edited.Id);
        Assert.Equal([trip.Id], edited.TagIds);
    }

    [Fact]
    public void SetTag_keeps_one_tag_and_null_clears_it()
    {
        var expense = new Expense(Guid.NewGuid(), M(10), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid());
        var trip = Guid.NewGuid();

        expense.SetTag(trip);
        Assert.Equal(trip, expense.TagId);

        expense.SetTag(null);
        Assert.Null(expense.TagId);
    }

    [Fact]
    public void Expense_tag_survives_a_snapshot_round_trip()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();
        var trip = account.AddTag("Trip");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(20), new DateOnly(2026, 1, 5), member, fund));
        expense.SetTag(trip.Id);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var rExpense = Assert.Single(restored.Periods[0].Expenses);
        Assert.Equal(trip.Id, rExpense.TagId);
    }

    [Fact]
    public void A_legacy_multi_tag_expense_collapses_to_its_most_used_tag_on_load()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();
        var trip = account.AddTag("Trip");
        var work = account.AddTag("Work");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        // One expense carries BOTH tags (legacy multi-tag); a second carries only Work, making Work the most-used.
        var multi = period.AddExpense(new Expense(food.Id, M(20), new DateOnly(2026, 1, 5), member, fund));
        multi.SetTags([trip.Id, work.Id]);
        var workOnly = period.AddExpense(new Expense(food.Id, M(5), new DateOnly(2026, 1, 6), member, fund));
        workOnly.SetTag(work.Id);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        // The multi-tag expense collapses to Work (used twice) rather than Trip (once).
        var rMulti = restored.Periods[0].Expenses.Single(e => e.Amount.Amount == 20m);
        Assert.Equal(work.Id, rMulti.TagId);
    }

    [Fact]
    public void An_untagged_expense_round_trips_with_no_tag()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(food.Id, M(20), new DateOnly(2026, 1, 5), Guid.NewGuid(), Guid.NewGuid()));

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Null(restored.Periods[0].Expenses[0].TagId);
    }
}
