using FinApp.Domain.Accounts;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Recurring;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class AccountForecastTests
{
    private const string Eur = "EUR";

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
}
