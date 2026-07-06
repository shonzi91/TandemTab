using FinApp.Domain.Budgeting;
using Xunit;

namespace FinApp.Domain.Tests;

public class BankDuplicateMatcherTests
{
    private static BankDuplicateMatcher.Pending Debit(string id, decimal amount, int day) =>
        new(id, amount, new DateOnly(2026, 1, day));
    private static BankDuplicateMatcher.Entry Entry(Guid id, decimal amount, int day) =>
        new(id, amount, new DateOnly(2026, 1, day));

    [Fact]
    public void Pairs_same_amount_within_the_window()
    {
        var exp = Guid.NewGuid();
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -45m, 5) },
            new[] { Entry(exp, 45m, 3) },   // 2 days earlier — within ±4
            windowDays: 4);

        Assert.Single(s);
        Assert.Equal("t1", s[0].ExternalId);
        Assert.Equal(exp, s[0].ExpenseId);
        Assert.Equal(2, s[0].DayGap);
    }

    [Fact]
    public void Ignores_amount_mismatches_and_out_of_window_dates()
    {
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -45m, 5) },
            new[] { Entry(Guid.NewGuid(), 40m, 5), Entry(Guid.NewGuid(), 45m, 20) },
            windowDays: 4);

        Assert.Empty(s);
    }

    [Fact]
    public void Matches_per_occurrence_not_by_existence()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // Two identical bank debits, but only one matching entry → only one pairs.
        var one = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -10m, 4), Debit("t2", -10m, 4) },
            new[] { Entry(a, 10m, 4) },
            windowDays: 4);
        Assert.Single(one);

        // Two entries → both pair, each entry claimed once.
        var two = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -10m, 4), Debit("t2", -10m, 4) },
            new[] { Entry(a, 10m, 4), Entry(b, 10m, 5) },
            windowDays: 4);
        Assert.Equal(2, two.Count);
        Assert.Equal(2, two.Select(x => x.ExpenseId).Distinct().Count());
    }

    [Fact]
    public void Nearest_date_wins_when_several_entries_share_the_amount()
    {
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -20m, 10) },
            new[] { Entry(far, 20m, 7), Entry(near, 20m, 9) },
            windowDays: 4);

        Assert.Single(s);
        Assert.Equal(near, s[0].ExpenseId);   // 1 day gap beats 3
    }
}
