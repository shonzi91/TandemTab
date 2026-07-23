using FinApp.Domain.Accounts;
using FinApp.Domain.Recurring;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>The shared recurring-posting path (<see cref="FinApp.Domain.Periods.Period.PostRecurring"/>) used by both
/// the web confirm/auto-post and the server-side confirm endpoint.</summary>
public class PeriodPostRecurringTests
{
    private static Account NewAccount()
    {
        var account = new Account("A", "EUR");
        account.AddDefaultFunds();
        return account;
    }

    [Fact]
    public void Posts_an_expense_at_the_due_date_and_marks_handled()
    {
        var account = NewAccount();
        var cat = account.AddCategory("Rent").Id;
        var fund = account.FundId("Bank");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var item = new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 500m, 15, cat, fund);

        period.PostRecurring(item, 520m, Guid.NewGuid(), fundSynced: false);

        var expense = Assert.Single(period.Expenses);
        Assert.Equal(520m, expense.Amount.Amount);
        Assert.Equal(cat, expense.CategoryId);
        Assert.Equal(new DateOnly(2026, 1, 15), expense.Date);
        Assert.Equal(period.From, item.LastHandledPeriodFrom);
    }

    [Fact]
    public void Posts_income_as_a_contribution()
    {
        var account = NewAccount();
        var cat = account.AddContributionCategory("Salary").Id;
        var fund = account.FundId("Bank");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var item = new RecurringItem("Salary", RecurringKind.Income, RecurringAmountMode.Fixed, 2000m, 1, cat, fund);

        period.PostRecurring(item, 2000m, Guid.NewGuid(), fundSynced: false);

        Assert.Empty(period.Expenses);
        Assert.Equal(2000m, period.ContributionsPaidTotal.Amount);
        Assert.Equal(period.From, item.LastHandledPeriodFrom);
    }

    [Fact]
    public void A_zero_amount_posts_nothing_but_still_marks_handled()
    {
        var account = NewAccount();
        var cat = account.AddCategory("Rent").Id;
        var fund = account.FundId("Bank");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var item = new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.ReminderOnly, 0m, 15, cat, fund);

        period.PostRecurring(item, 0m, Guid.NewGuid(), fundSynced: false);

        Assert.Empty(period.Expenses);
        Assert.Equal(period.From, item.LastHandledPeriodFrom);
    }
}
