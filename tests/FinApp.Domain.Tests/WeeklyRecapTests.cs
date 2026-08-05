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
}
