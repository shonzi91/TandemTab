using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class InsightsServiceTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly Func<Money, string> NoFmt = static _ => string.Empty;

    [Fact]
    public void An_empty_period_has_no_data_to_score()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.False(new InsightsService().Build(account, 0, NoFmt).HasData);
    }

    [Fact]
    public void An_out_of_range_period_index_returns_an_empty_report()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();

        Assert.False(new InsightsService().Build(account, 0, NoFmt).HasData);   // no periods at all
    }

    [Fact]
    public void Income_and_spending_produce_a_scored_report_with_a_category_breakdown()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(2000));
        p.AddExpense(new Expense(food, M(500), new DateOnly(2026, 1, 10), Guid.NewGuid(), fund));

        var report = new InsightsService().Build(account, 0, NoFmt);

        Assert.True(report.HasData);
        Assert.InRange(report.Score, 0, 100);
        var foodRow = Assert.Single(report.Breakdown, c => c.Name == "Food");
        // The breakdown carries the category's RAW stored icon — null here (no explicit icon), NOT a guessed display
        // icon. Resolving to a display icon is now the client's job (CategoryIcons decoupled from the domain service).
        Assert.Null(foodRow.Icon);
    }
}
