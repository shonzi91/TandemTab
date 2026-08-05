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
        Assert.Equal(M(2000), ov.MoneyIn);        // nothing carried in, so money-in is the fresh income
        Assert.Equal(M(300), ov.SavedThisPeriod);
        Assert.Equal(0.15m, ov.SavedRate);        // 300 / 2000
    }

    [Fact]
    public void Money_in_carries_the_prior_period_over_and_the_rate_measures_against_it()
    {
        // The point of MoneyIn: a second period sets aside money that mostly CARRIED OVER rather than arriving.
        // Measured against fresh income alone the rate would read 100%; against money-in it is the honest 25%.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var fund = account.FundId("Bank");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var first = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        first.Deposit(member.UserId, M(1500), fundId: fund);
        first.Close();
        var second = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        second.SetInitialBalance(fund, M(1500));           // what January closed with, carried in
        second.Deposit(member.UserId, M(500), fundId: fund);
        second.AllocateToSavings(Guid.NewGuid(), M(500), new DateOnly(2026, 2, 3));

        var ov = AccountOverview.For(account, second);

        Assert.Equal(M(500), ov.Contributed);       // only what actually arrived in February
        Assert.Equal(M(2000), ov.MoneyIn);          // ...plus the 1500 carried in
        Assert.Equal(M(1500), ov.MoneyIn - ov.Contributed);   // the "+X carried" the hero breaks out
        Assert.Equal(0.25m, ov.SavedRate);          // 500 / 2000, not 500 / 500
    }

    [Fact]
    public void Transfers_out_are_reported_apart_from_spend()
    {
        // Spent must stay expenses-only (budget bars and the health score read it), so the hero gets the transfer
        // half as its own figure. Reporting one number would make moving money to another account look like a
        // spending blow-out.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var category = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000), fundId: fund);
        period.AddExpense(new Expense(category, M(200), new DateOnly(2026, 1, 5), member.UserId, fund));
        period.TransferOut(fund, M(300), new DateOnly(2026, 1, 6), Guid.NewGuid(), "to Shared");

        var ov = AccountOverview.For(account, period);

        Assert.Equal(M(200), ov.Spent);                        // expenses only
        Assert.Equal(M(300), ov.TransfersOut);
        Assert.Equal(M(500), ov.Spent + ov.TransfersOut);      // what the hero's "Spent" tile totals
    }

    [Fact]
    public void The_rate_is_null_when_nothing_came_in()
    {
        // A brand-new period with no income and no carry-over: the client must show no rate at all rather than
        // divide by zero or print "0% of money in", which reads as a judgement on someone who has just started.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var ov = AccountOverview.For(account, period);

        Assert.Equal(M(0), ov.MoneyIn);
        Assert.Null(ov.SavedRate);
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
