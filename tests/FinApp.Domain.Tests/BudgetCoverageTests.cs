using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class BudgetCoverageTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>
    /// ★ Owner ask: money sent to another account already counted in every "money out" total the app shows, but it
    /// reached no budget — a budget caps a category and a transfer had none. So a standing household transfer sat
    /// inside "Spent" while belonging to no plan. Naming a category is what makes it plannable; leaving it unnamed
    /// must keep the old behaviour exactly.
    /// </summary>
    [Fact]
    public void A_categorised_transfer_out_counts_against_that_budget_and_an_uncategorised_one_does_not()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var household = account.AddCategory("Household");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        p.Deposit(me.UserId, M(2000), fundId: bank);
        p.AddBudget(household.Id, M(500));
        p.AddExpense(new Expense(household.Id, M(120), new DateOnly(2026, 4, 3), me.UserId, bank, "Cleaning"));

        var svc = new BudgetCoverageService();
        Assert.Equal(M(120), svc.ForCategory(account, p, household.Id).Spent);

        // Uncategorised: money out, but nobody planned it — the budget must not move.
        p.TransferOut(bank, M(80), new DateOnly(2026, 4, 5), Guid.NewGuid(), "Pocket money");
        Assert.Equal(M(120), svc.ForCategory(account, p, household.Id).Spent);

        // Filed under Household: now it is spending against that plan.
        var planned = p.TransferOut(bank, M(400), new DateOnly(2026, 4, 10), Guid.NewGuid(), "To the joint account");
        planned.SetCategory(household.Id);

        Assert.Equal(M(520), svc.ForCategory(account, p, household.Id).Spent);
        Assert.True(svc.ForCategory(account, p, household.Id).IsOverBudget);

        // ⚠️ And the header the rows sum to has to move with them, or one screen holds two answers.
        Assert.Equal(M(400), p.CategorisedTransfersOutTotal);
        Assert.Equal(M(120), p.ExpensesTotal);
        Assert.Equal(M(480), p.AccountTransfersOutTotal);   // every transfer, categorised or not, still counts as money out
    }

    [Fact]
    public void Coverage_is_unchanged_when_an_old_sub_category_is_flattened_into_its_parent()
    {
        // The roll-up this used to test is gone with sub-categories, but the figure it protected still matters:
        // an account that read "180 of 200" before the conversion has to read exactly that after it.
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var kids = account.AddCategory("Kids");
        var kid1 = account.AddCategory("Kid1");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(kids.Id, M(200), alertThreshold: 0.80m);

        // Spend 100 directly on Kids, 80 on what used to be the Kid1 sub-category => 180 of 200.
        period.AddExpense(new Expense(kids.Id, M(100), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid()));
        period.AddExpense(new Expense(kid1.Id, M(80), new DateOnly(2026, 1, 6), member.UserId, Guid.NewGuid()));

        var loaded = CategoryFlattenTests.LoadAsLegacy(account, "Kid1", kids.Id);
        var coverage = new BudgetCoverageService().ForCategory(loaded, loaded.Periods[0], kids.Id);

        Assert.Equal(M(180), coverage.Spent);
        Assert.Equal(M(20), coverage.Remaining);
        Assert.Equal(90, coverage.Percent);
        Assert.True(coverage.ThresholdReached);  // 90% >= 80%
        Assert.False(coverage.IsOverBudget);
    }

    [Fact]
    public void Flags_overspend()
    {
        var account = new Account("Personal", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(100));
        period.AddExpense(new Expense(food.Id, M(130), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid()));

        var coverage = new BudgetCoverageService().ForCategory(account, period, food.Id);

        Assert.True(coverage.IsOverBudget);
        Assert.Equal(M(-30), coverage.Remaining);
        Assert.Equal(130, coverage.Percent);
    }

    [Fact]
    public void Below_threshold_does_not_alert()
    {
        var account = new Account("Personal", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var fun = account.AddCategory("Entertainment");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(fun.Id, M(100), alertThreshold: 0.80m);
        period.AddExpense(new Expense(fun.Id, M(50), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid()));

        var coverage = new BudgetCoverageService().ForCategory(account, period, fun.Id);

        Assert.False(coverage.ThresholdReached);
        Assert.Equal(50, coverage.Percent);
    }
}
