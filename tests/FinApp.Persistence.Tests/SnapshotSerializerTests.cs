using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Contracts;
using FinApp.Persistence;
using Xunit;

namespace FinApp.Persistence.Tests;

public class SnapshotSerializerTests
{
    private static Money Eur(decimal v) => new(v, "EUR");

    private static Account BuildRichAccount(out Guid expenseFromSavingsId)
    {
        var owner = Guid.NewGuid();
        var account = new Account("Family", "EUR");
        account.AssignOwner(owner, "Owner");
        account.SetSavingsRateTarget(0.30m);
        var partner = account.AddContributor(Guid.NewGuid(), "Partner");

        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");
        account.SetFundIcon(bank, "🏦");

        var salary = account.AddContributionCategory("Salary");
        account.SetContributionCategoryIcon(salary.Id, "💼");

        var food = account.AddCategory("Food", icon: "🍽️");
        account.AddCategory("Groceries", food.Id); // nested, no explicit icon
        var fun = account.AddCategory("Fun");

        var vacations = account.AddSavingCategory("Vacations");
        account.ConfigureSavingGoal(vacations.Id, 2000m, 0.75m, notifyOnMilestone: true);
        account.SetSavingCategoryIcon(vacations.Id, "🏖️");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(bank, Eur(1000));
        period.SetInitialBalance(cash, Eur(200));
        period.Deposit(owner, Eur(600));
        period.AddBudget(food.Id, Eur(400), 0.9m, notifyOnEveryExpense: true);
        period.AddExpense(new Expense(food.Id, Eur(45), new DateOnly(2026, 1, 4), owner, bank, "Lunch"));
        period.TransferFunds(bank, cash, Eur(100), new DateOnly(2026, 1, 6), "top up wallet");
        period.AllocateToSavings(vacations.Id, Eur(150), new DateOnly(2026, 1, 7), "set aside");
        var fromSavings = period.ConvertSavingToExpense(vacations.Id, fun.Id, Eur(50), new DateOnly(2026, 1, 9), partner.UserId, cash, "day trip");
        expenseFromSavingsId = fromSavings.Id;

        return account;
    }

    [Fact]
    public void Round_trips_the_achievements_anchor_and_log()
    {
        var account = new Account("Ach", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Owner");
        account.SetAchievementsAnchor(new DateOnly(2026, 7, 1));
        account.RecordAchievement("first_expense", new DateOnly(2026, 7, 3));
        account.RecordAchievement("saver", new DateOnly(2026, 7, 5));

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Equal(new DateOnly(2026, 7, 1), copy.AchievementsAnchor);
        Assert.Equal(2, copy.AchievementLog.Count);
        Assert.Equal(new DateOnly(2026, 7, 3), copy.AchievementLog["first_expense"]);
        Assert.Equal(new DateOnly(2026, 7, 5), copy.AchievementLog["saver"]);
    }

    [Fact]
    public void Legacy_snapshot_without_achievements_defaults_to_empty()
    {
        var account = new Account("Legacy", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Owner");

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Null(copy.AchievementsAnchor);
        Assert.Empty(copy.AchievementLog);
    }

    [Fact]
    public void Round_trips_the_full_aggregate_preserving_ids_and_links()
    {
        var original = BuildRichAccount(out var savingsExpenseId);

        var json = AccountSnapshotSerializer.Serialize(original);
        var copy = AccountSnapshotSerializer.Deserialize(json);

        // Header
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Currency, copy.Currency);
        Assert.Equal(0.30m, copy.SavingsRateTarget);
        Assert.Equal(original.OwnerUserId, copy.OwnerUserId);
        Assert.True(copy.IsOwner(original.OwnerUserId));
        Assert.Equal(original.Members.Select(m => (m.UserId, m.DisplayName)),
                     copy.Members.Select(m => (m.UserId, m.DisplayName)));

        // Funds & categories (ids preserved so references resolve)
        Assert.Equal(original.Funds.Select(f => (f.Id, f.Name)), copy.Funds.Select(f => (f.Id, f.Name)));
        // Icons round-trip on funds, buckets and contribution categories
        Assert.Equal("🏦", copy.Funds.Single(f => f.Name == "Bank").Icon);
        Assert.Equal("🏖️", copy.SavingCategories.Single(s => s.Name == "Vacations").Icon);
        Assert.Equal("💼", copy.ContributionCategories.Single(c => c.Name == "Salary").Icon);
        Assert.Equal(original.Categories.Select(c => (c.Id, c.Name, c.ParentId, c.Icon)),
                     copy.Categories.Select(c => (c.Id, c.Name, c.ParentId, c.Icon)));
        Assert.Equal("🍽️", copy.Categories.Single(c => c.Name == "Food").Icon);
        Assert.Null(copy.Categories.Single(c => c.Name == "Groceries").Icon);

        // Savings goal
        var savCopy = copy.SavingCategories.Single();
        Assert.Equal(2000m, savCopy.GoalAmount);
        Assert.Equal(0.75m, savCopy.AlertThreshold);
        Assert.True(savCopy.NotifyOnMilestone);

        // Period & computed values must match exactly (proves links survived)
        var op = original.Periods.Single();
        var cp = copy.Periods.Single();
        Assert.Equal(op.Id, cp.Id);
        Assert.Equal(op.ExpectedClosingBalance, cp.ExpectedClosingBalance);
        Assert.Equal(op.ExpensesTotal, cp.ExpensesTotal);
        Assert.Equal(op.SavingsNetTotal, cp.SavingsNetTotal);
        Assert.Equal(op.ContributionsPaidTotal, cp.ContributionsPaidTotal);
        Assert.Equal(op.FundBalance(original.FundId("Bank")), cp.FundBalance(copy.FundId("Bank")));

        // The savings-funded expense keeps its drawdown link (SourceExpenseId -> expense id)
        Assert.Contains(cp.SavingAllocations, a => a.SourceExpenseId == savingsExpenseId);
        Assert.Contains(cp.Expenses, e => e.Id == savingsExpenseId && e.SourceSavingCategoryId is not null);
    }

    [Fact]
    public void Closed_period_status_survives()
    {
        var account = new Account("Solo", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Close();

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(PeriodStatus.Closed, copy.Periods.Single().Status);
    }

    [Fact]
    public void Synced_fund_flag_and_per_entry_markers_round_trip()
    {
        var owner = Guid.NewGuid();
        var account = new Account("Home", "EUR");
        account.AssignOwner(owner, "Me");
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        account.Funds.Single(f => f.Id == bank).SetSynced(true);
        var food = account.AddCategory("Food");

        var cash = account.FundId("Cash");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.SetInitialBalance(bank, Eur(1000));
        p.SetInitialBalance(cash, Eur(500));
        var e = new Expense(food.Id, Eur(40), new DateOnly(2026, 1, 4), owner, bank);
        e.SetFundSynced(true);
        e.SetBankLink("txn-abc-123", autoFiled: true);   // imported + auto-filed by a merchant rule
        p.AddExpense(e);

        // An auto-filed money-in: transfer from Cash into the synced Bank fund.
        var moneyIn = p.TransferFunds(cash, bank, Eur(200), new DateOnly(2026, 1, 8));
        moneyIn.SetSyncedSides(fromSynced: false, toSynced: true);
        moneyIn.SetBankLink("txn-in-999", autoFiled: true);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.True(copy.Funds.Single(f => f.Id == bank).IsSynced);
        var copiedExpense = copy.Periods.Single().Expenses.Single();
        Assert.True(copiedExpense.FundSynced);
        Assert.Equal("txn-abc-123", copiedExpense.BankExternalId);   // bank provenance survives (dedupe key)
        Assert.True(copiedExpense.AutoFiled);                        // auto-filed marker survives

        var copiedTransfer = copy.Periods.Single().FundTransfers.Single();
        Assert.True(copiedTransfer.ToSynced);
        Assert.Equal("txn-in-999", copiedTransfer.BankExternalId);   // transfer provenance survives too
        Assert.True(copiedTransfer.AutoFiled);
        Assert.Equal(Eur(1000), copy.Periods.Single().FundBalance(bank));   // synced expense + synced-dest transfer excluded
    }

    [Fact]
    public void Category_essential_flag_round_trips()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var rent = account.AddCategory("Rent");
        account.SetCategoryEssential(rent.Id, true);
        var fun = account.AddCategory("Fun");   // stays discretionary

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.True(copy.Categories.Single(c => c.Name == "Rent").IsEssential);
        Assert.False(copy.Categories.Single(c => c.Name == "Fun").IsEssential);
    }

    [Fact]
    public void Debt_bucket_kind_and_figures_round_trip()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(car.Id, balance: 12_000m, annualRatePercent: 6.5m, installment: 300m);
        account.RecordSavingDebtPayment(car.Id, 2_000m);   // remaining 10k, original stays 12k
        account.SetSavingPlannedContribution(car.Id, 350m);
        account.SetSavingArchived(car.Id, true);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.SavingCategories.Single(s => s.Name == "Car loan");
        Assert.True(copied.IsDebt);
        Assert.Equal(10_000m, copied.DebtBalance);
        Assert.Equal(6.5m, copied.DebtAnnualRatePercent);
        Assert.Equal(300m, copied.DebtInstallment);
        Assert.Equal(12_000m, copied.DebtOriginalBalance);   // original owed survives the round-trip
        Assert.Equal(2_000m, copied.DebtPaidOff);
        Assert.Equal(350m, copied.PlannedContribution);
        Assert.True(copied.IsArchived);
    }

    [Fact]
    public void Legacy_debt_snapshot_without_original_balance_baselines_progress_at_current()
    {
        // A debt node written before DebtOriginalBalance existed → the field is absent (0). On read it must
        // back-fill to the current balance so progress starts at 0% rather than dividing by zero.
        var legacy = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"Old","Currency":"EUR",
             "OwnerUserId":"22222222-2222-2222-2222-222222222222","Members":[],"Funds":[],
             "Categories":[],"SavingCategories":[
               {"Id":"33333333-3333-3333-3333-333333333333","Name":"Loan","ParentId":null,
                "GoalAmount":null,"AlertThreshold":0.8,"NotifyOnMilestone":false,"InitialAmount":0,
                "Icon":null,"Kind":1,"DebtBalance":8000,"DebtAnnualRatePercent":5,"DebtInstallment":200,
                "IsArchived":false}],
             "Periods":[]}
            """;
        var account = AccountSnapshotSerializer.Deserialize(legacy);
        var loan = account.SavingCategories.Single();
        Assert.Equal(8000m, loan.DebtOriginalBalance);
        Assert.Equal(0m, loan.DebtPaidOff);
        Assert.Equal(0m, loan.DebtProgressRatio);
        Assert.Null(loan.PlannedContribution);
    }

    [Fact]
    public void Recurring_items_round_trip()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var food = account.AddCategory("Food");
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");

        var rent = account.AddRecurring(new FinApp.Domain.Recurring.RecurringItem(
            "Rent", FinApp.Domain.Recurring.RecurringKind.Expense, FinApp.Domain.Recurring.RecurringAmountMode.Fixed,
            900m, 1, food.Id, bank, "🏠"));
        var elec = account.AddRecurring(new FinApp.Domain.Recurring.RecurringItem(
            "Electricity", FinApp.Domain.Recurring.RecurringKind.Expense, FinApp.Domain.Recurring.RecurringAmountMode.Typical,
            64m, 15, food.Id, bank));
        elec.MarkHandled(new DateOnly(2026, 1, 1));
        elec.SetActive(false);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Equal(2, copy.RecurringItems.Count);
        var rentCopy = copy.FindRecurring(rent.Id)!;
        Assert.Equal("Rent", rentCopy.Name);
        Assert.Equal("🏠", rentCopy.Icon);
        Assert.Equal(FinApp.Domain.Recurring.RecurringAmountMode.Fixed, rentCopy.AmountMode);
        Assert.Equal(900m, rentCopy.ExpectedAmount);
        Assert.Equal(1, rentCopy.DayOfMonth);
        Assert.True(rentCopy.Active);

        var elecCopy = copy.FindRecurring(elec.Id)!;
        Assert.Equal(FinApp.Domain.Recurring.RecurringAmountMode.Typical, elecCopy.AmountMode);
        Assert.False(elecCopy.Active);
        Assert.Equal(new DateOnly(2026, 1, 1), elecCopy.LastHandledPeriodFrom);
        Assert.False(rentCopy.AutoPost);   // wasn't opted in
    }

    [Fact]
    public void Recurring_auto_post_flag_round_trips_and_only_applies_to_fixed()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var food = account.AddCategory("Food");
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");

        var rent = account.AddRecurring(new FinApp.Domain.Recurring.RecurringItem(
            "Rent", FinApp.Domain.Recurring.RecurringKind.Expense, FinApp.Domain.Recurring.RecurringAmountMode.Fixed,
            900m, 1, food.Id, bank, autoPost: true));
        // Auto-post is meaningless for a Typical (varying) amount → forced off.
        var elec = account.AddRecurring(new FinApp.Domain.Recurring.RecurringItem(
            "Electricity", FinApp.Domain.Recurring.RecurringKind.Expense, FinApp.Domain.Recurring.RecurringAmountMode.Typical,
            60m, 15, food.Id, bank, autoPost: true));
        Assert.True(rent.AutoPost);
        Assert.False(elec.AutoPost);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.True(copy.FindRecurring(rent.Id)!.AutoPost);
        Assert.False(copy.FindRecurring(elec.Id)!.AutoPost);
    }

    [Fact]
    public void Legacy_snapshot_without_recurring_defaults_to_empty()
    {
        var legacy = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"Old","Currency":"EUR",
             "OwnerUserId":"22222222-2222-2222-2222-222222222222","Members":[],"Funds":[],
             "Categories":[],"SavingCategories":[],"Periods":[]}
            """;
        var account = AccountSnapshotSerializer.Deserialize(legacy);
        Assert.Empty(account.RecurringItems);
    }

    [Fact]
    public void Legacy_snapshot_without_savings_target_defaults_to_20_percent()
    {
        // A snapshot produced before SavingsRateTarget existed has no such field.
        var legacy = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"Old","Currency":"EUR",
             "OwnerUserId":"22222222-2222-2222-2222-222222222222","Members":[],"Funds":[],
             "Categories":[],"SavingCategories":[],"Periods":[]}
            """;

        var account = AccountSnapshotSerializer.Deserialize(legacy);
        Assert.Equal(0.20m, account.SavingsRateTarget);
    }
}
