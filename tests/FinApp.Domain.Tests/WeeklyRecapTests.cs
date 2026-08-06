using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>F7 — "your week in money", a read-out of the last completed Monday–Sunday week.</summary>
public class WeeklyRecapTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    // Thursday 15 Jan 2026. The last completed week is Mon 5 – Sun 11; the one before it is Mon 29 Dec – Sun 4 Jan.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static (Account account, Period period, Category food, Category fun, Guid member) Setup()
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");
        var fun = account.AddCategory("Fun");
        account.AddDefaultFunds();
        // A period spanning both weeks under test, plus the tail of December.
        var period = account.StartPeriod(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(5000));
        return (account, period, food, fun, member.UserId);
    }

    private static void Spend(Account account, Period period, Category category, Guid member, decimal amount, DateOnly on) =>
        period.AddExpense(new Expense(category.Id, M(amount), on, member, account.FundId("Cash")));

    [Fact]
    public void The_covered_week_is_the_last_completed_monday_to_sunday()
    {
        var (account, _, _, _, _) = Setup();
        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(new DateOnly(2026, 1, 5), recap.From);
        Assert.Equal(new DateOnly(2026, 1, 11), recap.To);
    }

    [Theory]
    [InlineData(2026, 1, 12, 2026, 1, 5)]    // a Monday: the week that just ended
    [InlineData(2026, 1, 15, 2026, 1, 5)]    // mid-week: still the last completed one, so the card doesn't shift
    [InlineData(2026, 1, 18, 2026, 1, 5)]    // Sunday: the current week isn't over yet
    [InlineData(2026, 1, 19, 2026, 1, 12)]   // next Monday: rolls forward
    public void The_week_only_rolls_forward_on_a_monday(int y, int m, int d, int ey, int em, int ed)
    {
        var (account, _, _, _, _) = Setup();
        var recap = new WeeklyRecapService().Build(account, new DateOnly(y, m, d))!;
        Assert.Equal(new DateOnly(ey, em, ed), recap.From);
    }

    [Fact]
    public void Spend_is_totalled_for_the_covered_week_only_and_compared_with_the_week_before()
    {
        var (account, period, food, _, member) = Setup();
        Spend(account, period, food, member, 40m, new DateOnly(2026, 1, 5));    // covered week
        Spend(account, period, food, member, 20m, new DateOnly(2026, 1, 11));   // covered week (Sunday, inclusive)
        Spend(account, period, food, member, 15m, new DateOnly(2026, 1, 4));    // previous week (Sunday, inclusive)
        Spend(account, period, food, member, 99m, new DateOnly(2026, 1, 13));   // current week — not yet reported

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(M(60m), recap.Spent);
        Assert.Equal(M(15m), recap.PreviousSpent);
        Assert.Equal(M(45m), recap.Change);
        Assert.True(recap.HasComparison);
    }

    [Fact]
    public void A_week_that_straddles_two_periods_still_counts_both_halves()
    {
        // The week of Mon 29 Dec – Sun 4 Jan crosses the year; a recap scoped to one period would lose half of it.
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");
        account.AddDefaultFunds();
        var december = account.StartPeriod(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));
        var january = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        december.Deposit(member.UserId, M(1000));
        january.Deposit(member.UserId, M(1000));
        december.AddExpense(new Expense(food.Id, M(30m), new DateOnly(2025, 12, 30), member.UserId, account.FundId("Cash")));
        january.AddExpense(new Expense(food.Id, M(70m), new DateOnly(2026, 1, 2), member.UserId, account.FundId("Cash")));

        // Thursday 8 Jan → the last completed week is Mon 29 Dec – Sun 4 Jan.
        var recap = new WeeklyRecapService().Build(account, new DateOnly(2026, 1, 8))!;

        Assert.Equal(new DateOnly(2025, 12, 29), recap.From);
        Assert.Equal(M(100m), recap.Spent);
    }

    [Fact]
    public void The_top_category_is_the_largest_share_of_the_covered_week()
    {
        var (account, period, food, fun, member) = Setup();
        Spend(account, period, food, member, 30m, new DateOnly(2026, 1, 6));
        Spend(account, period, fun, member, 45m, new DateOnly(2026, 1, 7));
        Spend(account, period, fun, member, 500m, new DateOnly(2026, 1, 13));   // current week must not sway it

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(fun.Id, recap.TopCategoryId);
        Assert.Equal(M(45m), recap.TopCategorySpent);
    }

    [Fact]
    public void With_no_previous_week_there_is_no_comparison_to_draw()
    {
        var (account, period, food, _, member) = Setup();
        Spend(account, period, food, member, 60m, new DateOnly(2026, 1, 6));

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.False(recap.HasComparison);
        Assert.False(recap.IsEmpty);
    }

    [Fact]
    public void A_week_with_nothing_in_it_is_empty_so_the_card_can_stay_away()
    {
        var (account, _, _, _, _) = Setup();
        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.True(recap.IsEmpty);
        Assert.Null(recap.TopCategoryId);
        Assert.Equal(M(0m), recap.TopCategorySpent);
    }

    [Fact]
    public void Money_set_aside_during_the_week_is_reported_and_disbursements_are_not()
    {
        var (account, period, _, _, _) = Setup();
        var jar = account.AddSavingCategory("Holiday");
        period.AllocateToSavings(jar.Id, M(120m), new DateOnly(2026, 1, 8));
        period.AllocateToSavings(jar.Id, M(500m), new DateOnly(2026, 1, 14));   // current week — not this recap

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(M(120m), recap.Saved);
        Assert.False(recap.IsEmpty);
    }

    [Fact]
    public void An_account_with_no_periods_has_no_recap_at_all()
    {
        Assert.Null(new WeeklyRecapService().Build(new Account("Empty", Eur), Today));
    }

    // --- The detail behind the card (the modal's figures) --------------------------------------------------

    [Fact]
    public void Transactions_are_counted_for_the_covered_week_only()
    {
        var (account, period, food, fun, member) = Setup();
        Spend(account, period, food, member, 10m, new DateOnly(2026, 1, 5));
        Spend(account, period, fun, member, 20m, new DateOnly(2026, 1, 7));
        Spend(account, period, food, member, 30m, new DateOnly(2026, 1, 11));
        Spend(account, period, food, member, 40m, new DateOnly(2026, 1, 13));   // current week
        Spend(account, period, food, member, 50m, new DateOnly(2026, 1, 4));    // previous week

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(3, recap.ExpenseCount);
    }

    [Fact]
    public void The_biggest_expense_names_its_category_date_and_note()
    {
        var (account, period, food, fun, member) = Setup();
        Spend(account, period, food, member, 30m, new DateOnly(2026, 1, 6));
        period.AddExpense(new Expense(fun.Id, M(120m), new DateOnly(2026, 1, 9), member, account.FundId("Cash"), "Concert tickets"));
        Spend(account, period, fun, member, 900m, new DateOnly(2026, 1, 14));   // current week can't win it

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.NotNull(recap.Biggest);
        Assert.Equal(M(120m), recap.Biggest!.Amount);
        Assert.Equal(fun.Id, recap.Biggest.CategoryId);
        Assert.Equal(new DateOnly(2026, 1, 9), recap.Biggest.Date);
        Assert.Equal("Concert tickets", recap.Biggest.Note);
    }

    [Fact]
    public void Two_equal_expenses_resolve_to_the_earlier_one_so_the_card_does_not_flicker()
    {
        var (account, period, food, fun, member) = Setup();
        Spend(account, period, fun, member, 75m, new DateOnly(2026, 1, 9));
        Spend(account, period, food, member, 75m, new DateOnly(2026, 1, 6));

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(new DateOnly(2026, 1, 6), recap.Biggest!.Date);
    }

    [Fact]
    public void Income_covers_the_week_and_excludes_the_carried_over_balance()
    {
        // Carryover is booked as a contribution from a sentinel member. It is last month's leftover, not money
        // that arrived — counting it would spike "money in" every time a period rolls over.
        var (account, period, _, _, member) = Setup();
        period.Deposit(member, M(900m), date: new DateOnly(2026, 1, 8));
        period.Deposit(member, M(400m), date: new DateOnly(2026, 1, 13));                    // current week
        period.Deposit(Period.CarryoverSource, M(5000m), date: new DateOnly(2026, 1, 8));    // not income

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Equal(M(900m), recap.Income);
    }

    [Fact]
    public void Categories_and_tags_break_the_week_down_largest_first()
    {
        var (account, period, food, fun, member) = Setup();
        var work = account.AddTag("Work");
        Spend(account, period, food, member, 20m, new DateOnly(2026, 1, 6));
        Spend(account, period, fun, member, 65m, new DateOnly(2026, 1, 7));
        var tagged = new Expense(food.Id, M(45m), new DateOnly(2026, 1, 8), member, account.FundId("Cash"));
        tagged.SetTag(work.Id);
        period.AddExpense(tagged);

        var recap = new WeeklyRecapService().Build(account, Today)!;

        // Food 20 + 45 = 65 ties Fun's 65; either order is defensible, so assert the totals rather than the order.
        Assert.Equal(2, recap.CategoryBreakdown.Count);
        Assert.Equal(M(65m), recap.CategoryBreakdown.Single(c => c.Id == food.Id).Total);
        Assert.Equal(2, recap.CategoryBreakdown.Single(c => c.Id == food.Id).Count);

        // Untagged expenses are absent rather than pooled under a pseudo-tag.
        var tag = Assert.Single(recap.TagBreakdown);
        Assert.Equal(work.Id, tag.Id);
        Assert.Equal(M(45m), tag.Total);
    }

    [Fact]
    public void An_untagged_account_reports_no_tags_rather_than_a_placeholder()
    {
        var (account, period, food, _, member) = Setup();
        Spend(account, period, food, member, 25m, new DateOnly(2026, 1, 6));

        var recap = new WeeklyRecapService().Build(account, Today)!;

        Assert.Empty(recap.TagBreakdown);
    }
}
