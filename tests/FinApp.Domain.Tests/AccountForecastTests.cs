using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Recurring;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class AccountForecastTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Projects_from_recurring_items_when_there_is_no_closed_period()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var category = account.AddCategory("Rent").Id;
        var fund = account.FundId("Bank");
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        account.AddRecurring(new RecurringItem("Salary", RecurringKind.Income, RecurringAmountMode.Fixed, 3000m, 25, category, fund));
        account.AddRecurring(new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 1000m, 1, category, fund));

        var proj = AccountForecast.Runway(account);

        Assert.NotNull(proj);
        Assert.Equal(CashFlowBasis.Recurring, proj!.Basis);   // young account → recurring fallback
        Assert.Equal(3000m, proj.Months[0].Income);
        Assert.Equal(1000m, proj.Months[0].Spending);
        Assert.Null(proj.FirstShortfallMonth);                // income exceeds spending → never runs short
    }

    [Fact]
    public void Is_null_when_there_is_neither_history_nor_recurring()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.AddCategory("Food");
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Null(AccountForecast.Runway(account));   // no basis → say nothing
    }

    // --- Targets (the Home "on track for" reads) ---------------------------

    [Fact]
    public void Targets_date_a_savings_goal_at_its_demonstrated_pace()
    {
        var account = new Account("Home", Eur);
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var trip = account.AddSavingCategory("Trip");
        account.ConfigureSavingGoal(trip.Id, 1000m);

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(me.UserId, M(500));
        p1.AllocateToSavings(trip.Id, M(200), new DateOnly(2026, 1, 10));
        p1.Close();
        var p2 = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p2.Deposit(me.UserId, M(500));
        p2.AllocateToSavings(trip.Id, M(300), new DateOnly(2026, 2, 10));
        p2.Close();
        account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));   // current, open

        var targets = AccountForecast.Targets(account);

        var goal = Assert.Single(targets);
        Assert.Equal(TargetKind.Goal, goal.Kind);
        Assert.Equal("Trip", goal.Name);
        Assert.False(goal.Reached);
        // saved 500 (200+300 up to March), pace 250/period → ceil((1000-500)/250) = 2 months.
        Assert.Equal(2, goal.Months);
    }

    [Fact]
    public void A_goal_already_met_is_reported_reached_at_zero_months()
    {
        var account = new Account("Home", Eur);
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var emg = account.AddSavingCategory("Emergency");
        account.ConfigureSavingGoal(emg.Id, 400m);

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(me.UserId, M(600));
        p1.AllocateToSavings(emg.Id, M(500), new DateOnly(2026, 1, 10));
        p1.Close();
        account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));   // current, open

        var goal = Assert.Single(AccountForecast.Targets(account));
        Assert.True(goal.Reached);
        Assert.Equal(0, goal.Months);
    }

    [Fact]
    public void A_debt_bucket_yields_a_debt_free_month_count()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var loan = account.AddSavingCategory("Loan");
        account.ConfigureSavingDebt(loan.Id, balance: 1200m, annualRatePercent: 0m, installment: 100m);
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));   // current, open

        var target = Assert.Single(AccountForecast.Targets(account));
        Assert.Equal(TargetKind.DebtFree, target.Kind);
        Assert.False(target.Reached);
        Assert.Equal(12, target.Months);   // 1200 at 0% paying 100/mo clears in 12 months
    }

    [Fact]
    public void Targets_are_empty_when_there_is_nothing_to_project()
    {
        var account = new Account("Home", Eur);
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Empty(AccountForecast.Targets(account));
    }
}
