using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class AchievementsServiceTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void A_fresh_account_has_a_catalogue_but_nothing_earned_or_in_progress()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var counts = new AchievementsService().Counts(account);

        Assert.True(counts.Total > 0);       // the base catalogue always exists
        Assert.Equal(0, counts.Earned);
        Assert.Equal(0, counts.InProgress);
    }

    [Fact]
    public void Logging_the_first_expense_earns_that_milestone()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(food, M(20), new DateOnly(2026, 1, 5), Guid.NewGuid(), fund));

        var svc = new AchievementsService();
        Assert.True(svc.Build(account, static _ => string.Empty).Single(a => a.Key == "first_expense").Earned);
        Assert.True(svc.Counts(account).Earned >= 1);
    }

    [Fact]
    public void Sharing_the_account_earns_the_social_milestone()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.AddMember(Guid.NewGuid(), "Me");
        account.AddMember(Guid.NewGuid(), "Partner");
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.True(new AchievementsService().Build(account, static _ => string.Empty).Single(a => a.Key == "shared").Earned);
    }
}
