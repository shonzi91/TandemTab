using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Recurring;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class AccountOverviewTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Reports_the_header_figures_including_bills_still_due()
    {
        // €2000 in, €500 spent, €300 earmarked to savings, and a €200 recurring bill still due this period.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var category = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(2000), fundId: fund);
        period.AddExpense(new Expense(category, M(500), new DateOnly(2026, 1, 5), member.UserId, fund));
        period.AllocateToSavings(Guid.NewGuid(), M(300), new DateOnly(2026, 1, 6));
        account.AddRecurring(new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 200m, 15, category, fund));

        var ov = AccountOverview.For(account, period);

        Assert.Equal(M(1500), ov.Current);        // 2000 − 500 (savings don't leave the balance)
        Assert.Equal(M(1200), ov.Free);           // 1500 − 300 saved (budgets ignored)
        Assert.Equal(M(300), ov.Saved);           // current − free
        Assert.Equal(M(500), ov.Spent);
        Assert.Equal(M(2000), ov.Contributed);
        Assert.Equal(M(200), ov.BillsDue);        // the pending recurring bill
        Assert.Equal(M(1000), ov.SafeAfterBills); // 1200 − 200, can go negative
    }

    [Fact]
    public void Bills_due_is_zero_once_the_period_is_closed()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var category = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000), fundId: fund);
        account.AddRecurring(new RecurringItem("Rent", RecurringKind.Expense, RecurringAmountMode.Fixed, 200m, 15, category, fund));
        period.Close();

        var ov = AccountOverview.For(account, period);

        Assert.Equal(M(0), ov.BillsDue);              // a closed period has no bills "still due"
        Assert.Equal(ov.Free, ov.SafeAfterBills);     // so safe-to-spend equals free
    }
}
