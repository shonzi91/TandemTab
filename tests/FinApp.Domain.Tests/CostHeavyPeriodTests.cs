using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Owner ask #7, the half that can be stated honestly: WHICH periods ran heavy, and what drove each. These pin the
/// two choices that make it trustworthy — that "typical" is a median (a mean lets one blow-out hide itself), and
/// that the driver is the category unusual FOR ITSELF, not simply the biggest.
/// </summary>
public class CostHeavyPeriodTests
{
    private const string Eur = "EUR";

    private static Account NewAccount(out Guid rent, out Guid fun)
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        rent = account.AddCategory("Rent").Id;
        fun = account.AddCategory("Fun").Id;
        return account;
    }

    private static void Month(Account account, int month, params (Guid Cat, decimal Amount)[] spends)
    {
        var from = new DateOnly(2026, month, 1);
        var period = account.StartPeriod(from, new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
        foreach (var (cat, amount) in spends)
            period.AddExpense(new Expense(cat, new Money(amount, Eur), from, Guid.NewGuid(), account.FundId("Bank")));
        period.Close();
    }

    [Fact]
    public void Too_little_history_says_nothing_at_all()
    {
        // Below the minimum, "typical" describes nothing — so there is no baseline to call anything heavy against.
        var account = NewAccount(out var rent, out _);
        Month(account, 1, (rent, 1000m));
        Month(account, 2, (rent, 5000m));

        Assert.Empty(account.CostHeavyPeriods());
        Assert.Null(account.TypicalPeriodSpend());
    }

    [Fact]
    public void A_period_well_above_the_usual_is_reported_with_how_far_over_it_ran()
    {
        var account = NewAccount(out var rent, out var fun);
        Month(account, 1, (rent, 1000m));
        Month(account, 2, (rent, 1000m));
        Month(account, 3, (rent, 1000m));
        Month(account, 4, (rent, 1000m), (fun, 900m));   // the expensive one

        var heavy = Assert.Single(account.CostHeavyPeriods());
        Assert.Equal(new DateOnly(2026, 4, 1), heavy.From);
        Assert.Equal(1900m, heavy.Total);
        Assert.Equal(900m, heavy.Excess);                // 1900 over a typical 1000
        Assert.Equal(1000m, account.TypicalPeriodSpend());
    }

    [Fact]
    public void The_driver_is_the_category_unusual_for_itself_not_the_biggest_one()
    {
        // ★ Rent is the largest line in every period, so a "biggest category" rule would blame it for a month it
        // did not change in. The month is expensive because Fun quadrupled.
        var account = NewAccount(out var rent, out var fun);
        Month(account, 1, (rent, 1000m), (fun, 100m));
        Month(account, 2, (rent, 1000m), (fun, 100m));
        Month(account, 3, (rent, 1000m), (fun, 100m));
        Month(account, 4, (rent, 1000m), (fun, 900m));

        var heavy = Assert.Single(account.CostHeavyPeriods());
        Assert.Equal(fun, heavy.DriverCategoryId);
        Assert.Equal(800m, heavy.DriverExcess);          // 900 against its own usual 100
    }

    [Fact]
    public void One_blow_out_cannot_hide_itself_by_dragging_the_baseline_up()
    {
        // ★ Why the median and not the mean. Four ordinary months and one enormous one: the mean is pulled to
        // ~2800, which the 11,000 month would only be 3.9× of — but with a naive mean baseline a smaller spike
        // would slip under the threshold entirely. The median stays at the ordinary month.
        var account = NewAccount(out var rent, out _);
        Month(account, 1, (rent, 1000m));
        Month(account, 2, (rent, 1000m));
        Month(account, 3, (rent, 1000m));
        Month(account, 4, (rent, 1000m));
        Month(account, 5, (rent, 11_000m));

        Assert.Equal(1000m, account.TypicalPeriodSpend());   // not the ~3000 mean
        var heavy = Assert.Single(account.CostHeavyPeriods());
        Assert.Equal(10_000m, heavy.Excess);
    }

    [Fact]
    public void Steady_spending_flags_nothing()
    {
        var account = NewAccount(out var rent, out _);
        for (var m = 1; m <= 5; m++) Month(account, m, (rent, 1000m));

        Assert.Empty(account.CostHeavyPeriods());
    }

    [Fact]
    public void The_heaviest_period_is_reported_first()
    {
        var account = NewAccount(out var rent, out var fun);
        Month(account, 1, (rent, 1000m));
        Month(account, 2, (rent, 1000m));
        Month(account, 3, (rent, 1000m), (fun, 400m));
        Month(account, 4, (rent, 1000m), (fun, 1200m));

        var heavy = account.CostHeavyPeriods();
        Assert.Equal(2, heavy.Count);
        Assert.Equal(new DateOnly(2026, 4, 1), heavy[0].From);   // worst first
    }
}
