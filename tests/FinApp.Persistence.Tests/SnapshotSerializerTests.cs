using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
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
        // ⚠️ Was written as "Groceries nested under Food" and never was: AddCategory took a parent id and dropped
        // it. Top-level, like every category since the tree was flattened; what this row actually exercises is a
        // category with no explicit icon, which is the fallback the assertions below care about.
        account.AddCategory("Groceries");
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
    public void Onboarding_dismissed_flag_round_trips_and_defaults_false()
    {
        var fresh = new Account("Fresh", "EUR");
        fresh.AssignOwner(Guid.NewGuid(), "Owner");
        var freshCopy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(fresh));
        Assert.False(freshCopy.OnboardingDismissed);   // new account: checklist still shows

        fresh.DismissOnboarding();
        var dismissedCopy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(fresh));
        Assert.True(dismissedCopy.OnboardingDismissed);   // stays gone across reloads
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
    public void An_expense_time_round_trips_and_stays_null_when_nothing_recorded_one()
    {
        var owner = Guid.NewGuid();
        var account = new Account("Home", "EUR");
        account.AssignOwner(owner, "Me");
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var food = account.AddCategory("Food");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var timed = new Expense(food.Id, Eur(12), new DateOnly(2026, 1, 4), owner, bank, "Lunch");
        timed.SetTime(new TimeOnly(13, 42));
        p.AddExpense(timed);
        // The ordinary case: a bank feed that reports a date only. It must stay null rather than becoming 00:00,
        // which would print a time nobody reported and sort the row above everything logged that morning.
        p.AddExpense(new Expense(food.Id, Eur(9), new DateOnly(2026, 1, 4), owner, bank, "Coffee"));

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.Periods.Single().Expenses.ToList();
        Assert.Equal(new TimeOnly(13, 42), copied.Single(e => e.Note == "Lunch").Time);
        Assert.Null(copied.Single(e => e.Note == "Coffee").Time);
        // ...and an untimed row sorts last within its day under a newest-first sort, rather than first.
        Assert.Equal("Coffee", copied.OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime).Last().Note);
    }

    [Fact]
    public void A_refund_round_trips_and_an_untouched_expense_reports_none()
    {
        var owner = Guid.NewGuid();
        var account = new Account("Home", "EUR");
        account.AssignOwner(owner, "Me");
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var food = account.AddCategory("Food");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var dinner = p.AddExpense(new Expense(food.Id, Eur(60), new DateOnly(2026, 1, 4), owner, bank, "Dinner"));
        p.AddExpense(new Expense(food.Id, Eur(9), new DateOnly(2026, 1, 4), owner, bank, "Coffee"));
        p.SetRefund(dinner.Id, Eur(20));

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.Periods.Single().Expenses.ToList();
        var back = copied.Single(e => e.Note == "Dinner");
        Assert.Equal(20m, back.RefundedAmount);
        Assert.Equal(Eur(40), back.Amount);            // the stored amount is the reduced one
        Assert.Equal(Eur(60), back.AmountBeforeRefund);
        Assert.False(copied.Single(e => e.Note == "Coffee").IsRefunded);
        // The figure every total is built from survives the trip, which is the thing a lost field would break
        // quietly: the period would keep totalling the charge that was already partly paid back.
        Assert.Equal(Eur(49), copy.Periods.Single().ExpensesTotal);
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
    public void A_debt_schedule_anchor_round_trips_and_is_not_re_dated_on_load()
    {
        // The anchor is the date the balance was last true. Loading must restore it verbatim: re-dating it to
        // "now" on every open would make the schedule walk from today, freezing the balance forever.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var loan = account.AddSavingCategory("Mortgage");
        var anchored = new DateOnly(2026, 1, 15);
        account.ConfigureSavingDebt(loan.Id, balance: 20_000m, annualRatePercent: 6m, installment: 400m, balanceAsOf: anchored);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        var copied = copy.SavingCategories.Single(s => s.Name == "Mortgage");

        Assert.Equal(anchored, copied.DebtBalanceAsOf);
        // And the derived balance survives with it — a year on, principal has moved but not by 12 × €400.
        Assert.Equal(loan.DebtBalanceOn(anchored.AddMonths(12)), copied.DebtBalanceOn(anchored.AddMonths(12)));
        Assert.True(copied.DebtBalanceOn(anchored.AddMonths(12)) < 20_000m);
    }

    [Fact]
    public void Debt_installment_day_and_start_date_round_trip()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("Car loan");
        var started = new DateOnly(2024, 3, 10);
        account.ConfigureSavingDebt(car.Id, balance: 12_000m, annualRatePercent: 6.5m, installment: 300m,
            balanceAsOf: new DateOnly(2026, 1, 5), installmentDay: 10, startDate: started);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        var copied = copy.SavingCategories.Single(s => s.Name == "Car loan");

        Assert.Equal(10, copied.DebtInstallmentDay);
        Assert.Equal(started, copied.DebtStartDate);
        Assert.False(copied.DebtPaidInterestIsEstimate);   // start date survived → paid-interest stays exact
    }

    [Fact]
    public void An_installment_split_round_trips_with_its_group_parts_and_loan_link()
    {
        // The three rows are only one payment because they share a group id — lose that on save and the ledger holds
        // three unexplained expenses that can no longer be edited or removed together.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var fund = account.FundId("Bank");
        var loanCat = account.AddCategory("Loan").Id;
        var insCat = account.AddCategory("Insurance").Id;
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, 20_000m, 6m, 400m, balanceAsOf: new DateOnly(2026, 1, 1));
        account.SetSavingDebtPaymentDriven(loan.Id, true, new DateOnly(2026, 1, 1));
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var bucket = account.FindSavingCategory(loan.Id)!;
        var rows = period.LogInstallment(bucket, new Money(460m, "EUR"), new DateOnly(2026, 1, 15), Guid.NewGuid(), fund,
            loanCat, loanCat, additional: [new InstallmentExtra(new Money(60m, "EUR"), insCat)]);
        var groupId = rows[0].InstallmentGroupId!.Value;

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        var copiedRows = copy.Periods.Single().InstallmentGroup(groupId).ToList();

        Assert.Equal(3, copiedRows.Count);
        Assert.All(copiedRows, r => Assert.Equal(loan.Id, r.DebtBucketId));
        Assert.Equal(new Money(100m, "EUR"), copiedRows.Single(r => r.Part == InstallmentPart.Interest).Amount);
        Assert.Equal(new Money(300m, "EUR"), copiedRows.Single(r => r.Part == InstallmentPart.Principal).Amount);
        Assert.Equal(new Money(60m, "EUR"), copiedRows.Single(r => r.Part == InstallmentPart.Additional).Amount);
        // And the payment-driven flag survives — restored verbatim, so loading doesn't re-date the loan.
        var copiedLoan = copy.SavingCategories.Single(s => s.Name == "Car loan");
        Assert.True(copiedLoan.DebtPaymentDriven);
        Assert.Equal(19_700m, copiedLoan.DebtBalanceOn(new DateOnly(2026, 6, 15)));
    }

    [Fact]
    public void A_recurring_bills_debt_link_round_trips()
    {
        // Lose it on save and next month's bill quietly goes back to posting one lump expense.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Loan").Id;
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, 20_000m, 6m, 400m, balanceAsOf: new DateOnly(2026, 1, 1));
        var bill = new FinApp.Domain.Recurring.RecurringItem("Car loan", FinApp.Domain.Recurring.RecurringKind.Expense,
            FinApp.Domain.Recurring.RecurringAmountMode.Fixed, 400m, 15, category, account.FundId("Bank"));
        bill.SetLinkedDebtBucket(loan.Id);
        account.AddRecurring(bill);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account)).RecurringItems.Single();

        Assert.Equal(loan.Id, copied.LinkedDebtBucketId);
        Assert.True(copied.IsLoanInstallment);
    }

    [Fact]
    public void An_expenses_cross_account_trip_link_round_trips()
    {
        // Lose the account half and the row keeps a trip id pointing into the wrong account — which resolves to
        // nothing, so the attachment silently disappears from the trip that was counting it.
        var account = new Account("Mine", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Flights").Id;
        var period = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        period.SetInitialBalance(account.FundId("Bank"), new FinApp.Domain.Common.Money(3000m, "EUR"));
        var tripId = Guid.NewGuid();
        var hostAccountId = Guid.NewGuid();
        var expense = period.AddExpense(new FinApp.Domain.Budgeting.Expense(category,
            new FinApp.Domain.Common.Money(480m, "EUR"), new DateOnly(2026, 5, 20), Guid.NewGuid(),
            account.FundId("Bank"), "Flights"));
        expense.SetTrip(tripId, hostAccountId);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account))
            .CurrentPeriod!.Expenses.Single();

        Assert.Equal(tripId, copied.TripId);
        Assert.Equal(hostAccountId, copied.TripAccountId);
    }

    [Fact]
    public void A_trips_source_account_directory_round_trips()
    {
        // Lose it and the recap has no idea which accounts to look in — the trip renders as this account's spend
        // alone, which is a total that quietly shrank.
        var account = new Account("Joint", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7));
        var payerId = Guid.NewGuid();
        trip.AddSourceAccount(payerId);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account)).Trips.Single();

        Assert.Equal([payerId], copied.SourceAccountIds);
    }

    [Fact]
    public void A_bills_cross_account_loan_link_round_trips()
    {
        // Lose the account half and the bucket id is looked up here, finds nothing, and the next post books a
        // silent lump while the other account's balance stalls.
        var account = new Account("Mine", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Loan").Id;
        var ownerAccountId = Guid.NewGuid();
        var foreignBucketId = Guid.NewGuid();
        var bill = new FinApp.Domain.Recurring.RecurringItem("Mortgage", FinApp.Domain.Recurring.RecurringKind.Expense,
            FinApp.Domain.Recurring.RecurringAmountMode.Fixed, 600m, 10, category, account.FundId("Bank"));
        bill.SetLinkedDebtBucket(foreignBucketId, ownerAccountId);
        account.AddRecurring(bill);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account)).RecurringItems.Single();

        Assert.Equal(foreignBucketId, copied.LinkedDebtBucketId);
        Assert.Equal(ownerAccountId, copied.LinkedDebtAccountId);
        Assert.True(copied.IsCrossAccountInstallment);
    }

    [Fact]
    public void An_installment_rows_loan_account_round_trips()
    {
        // Without it the UNDO cannot find the bucket to reverse against — the rows would go and the other
        // account's balance would keep the payment.
        var account = new Account("Mine", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Loan").Id;
        var period = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        period.SetInitialBalance(account.FundId("Bank"), new FinApp.Domain.Common.Money(3000m, "EUR"));
        var ownerAccountId = Guid.NewGuid();
        var expense = period.AddExpense(new FinApp.Domain.Budgeting.Expense(category,
            new FinApp.Domain.Common.Money(500m, "EUR"), new DateOnly(2026, 6, 10), Guid.NewGuid(),
            account.FundId("Bank"), "Mortgage"));
        expense.SetInstallmentLink(Guid.NewGuid(), FinApp.Domain.Budgeting.InstallmentPart.Principal,
            Guid.NewGuid(), ownerAccountId);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account))
            .CurrentPeriod!.Expenses.Single();

        Assert.Equal(ownerAccountId, copied.DebtBucketAccountId);
    }

    [Fact]
    public void A_recurring_bills_excess_line_round_trips()
    {
        // Lose it and next month's €700 quietly goes back to being €700 of loan servicing, dropping ~€100 more
        // principal than the schedule says — silently, on the next post, with nobody having touched anything.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Loan").Id;
        var insurance = account.AddCategory("Insurance").Id;
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, 20_000m, 6m, 600m, balanceAsOf: new DateOnly(2026, 1, 1));
        var bill = new FinApp.Domain.Recurring.RecurringItem("Car loan", FinApp.Domain.Recurring.RecurringKind.Expense,
            FinApp.Domain.Recurring.RecurringAmountMode.Fixed, 700m, 15, category, account.FundId("Bank"));
        bill.SetLinkedDebtBucket(loan.Id);
        bill.SetExcess(insurance, "Health + property");
        account.AddRecurring(bill);

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account)).RecurringItems.Single();

        Assert.Equal(insurance, copied.ExcessCategoryId);
        Assert.Equal("Health + property", copied.ExcessLabel);
        Assert.Equal(100m, copied.ExcessOn(700m, 600m));
    }

    [Fact]
    public void A_bill_written_before_the_excess_line_existed_loads_with_none()
    {
        // The trailing-optional contract, from the other end: a legacy node carries no excess fields, and "we were
        // never told" must load as "all of it services the loan" — the behaviour that snapshot was written under.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Loan").Id;
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, 20_000m, 6m, 600m, balanceAsOf: new DateOnly(2026, 1, 1));
        var bill = new FinApp.Domain.Recurring.RecurringItem("Car loan", FinApp.Domain.Recurring.RecurringKind.Expense,
            FinApp.Domain.Recurring.RecurringAmountMode.Fixed, 700m, 15, category, account.FundId("Bank"));
        bill.SetLinkedDebtBucket(loan.Id);
        account.AddRecurring(bill);

        // Strip the two fields from the payload the way a pre-C server would never have written them.
        var payload = AccountSnapshotSerializer.Serialize(account)
            .Replace(",\"ExcessCategoryId\":null", "").Replace(",\"ExcessLabel\":null", "");
        var copied = AccountSnapshotSerializer.Deserialize(payload).RecurringItems.Single();

        Assert.Null(copied.ExcessCategoryId);
        Assert.Null(copied.ExcessLabel);
        Assert.Equal(0m, copied.ExcessOn(700m, 600m));
    }

    [Fact]
    public void An_ordinary_expense_round_trips_with_no_installment_fields()
    {
        // Legacy snapshots carry no installment fields at all; they must load as plain expenses, not empty groups.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        account.AddDefaultFunds();
        var category = account.AddCategory("Food").Id;
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddExpense(new Expense(category, new Money(12m, "EUR"), new DateOnly(2026, 1, 6), Guid.NewGuid(), account.FundId("Bank")));

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account))
            .Periods.Single().Expenses.Single();

        Assert.Null(copied.InstallmentGroupId);
        Assert.Null(copied.Part);
        Assert.Null(copied.DebtBucketId);
        Assert.False(copied.IsInstallmentPart);
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
        Assert.Null(copied.DebtBalanceAsOf);                 // never anchored → stays unanchored, as before
        Assert.True(copied.IsArchived);
    }

    [Fact]
    public void Investment_bucket_kind_and_figures_round_trip()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var fund = account.AddSavingCategory("Index fund");
        account.ConfigureSavingInvestment(fund.Id, annualRatePercent: 7m, termYears: 20m, compoundsPerYear: 12);
        account.SetSavingInitialAmount(fund.Id, 5_000m);
        account.SetSavingPlannedContribution(fund.Id, 300m);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.SavingCategories.Single(s => s.Name == "Index fund");
        Assert.True(copied.IsInvestment);
        Assert.False(copied.IsDebt);
        Assert.Equal(7m, copied.InvestmentAnnualRatePercent);
        Assert.Equal(20m, copied.InvestmentTermYears);
        Assert.Equal(12, copied.InvestmentCompoundsPerYear);
        Assert.Equal(5_000m, copied.InitialAmount);
        Assert.Equal(300m, copied.PlannedContribution);
    }

    [Fact]
    public void Expenses_fund_cost_list_round_trips()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var fund = account.AddFund("Main");
        var car = account.AddSavingCategory("Car");
        account.SetSavingFund(car.Id, fund.Id);
        account.SetSavingCosts(car.Id, new[]
        {
            new PlannedCost("Insurance", 400m, CostCadence.Yearly),
            new PlannedCost("Road tax", 180m, CostCadence.Yearly),
            new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 6, 1)),
        });

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.SavingCategories.Single(s => s.Name == "Car");
        Assert.Equal(fund.Id, copied.FundId);
        Assert.Equal(3, copied.Costs.Count);
        Assert.Equal(new PlannedCost("Residual", 3_000m, CostCadence.OneOff, new DateOnly(2027, 6, 1)),
            copied.Costs.Single(c => c.Label == "Residual"));
    }

    [Fact]
    public void Archived_fund_round_trips_and_keeps_its_history()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var bank = account.AddFund("Bank");
        var oldCard = account.AddFund("Old card");
        account.SetFundArchived(oldCard.Id, true);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.True(copy.FindFund(oldCard.Id)!.IsArchived);
        Assert.False(copy.FindFund(bank.Id)!.IsArchived);
        Assert.Equal("Old card", copy.FundName(oldCard.Id));   // archived funds still resolve by name for history
    }

    [Fact]
    public void Legacy_planned_expense_kind_restores_as_a_common_bucket_keeping_its_goal()
    {
        // Kind value 3 was a short-lived "PlannedExpense" kind. Snapshots from that build encode it as the
        // integer 3; after its removal it must restore cleanly as a normal (Common) savings bucket, goal intact.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("New car");
        account.ConfigureSavingGoal(car.Id, 8_000m);

        var json = AccountSnapshotSerializer.Serialize(account).Replace("\"Kind\":0", "\"Kind\":3");
        var copy = AccountSnapshotSerializer.Deserialize(json);

        var copied = copy.SavingCategories.Single(s => s.Name == "New car");
        Assert.Equal(SavingKind.Common, copied.Kind);
        Assert.False(copied.IsDebt);
        Assert.False(copied.IsInvestment);
        Assert.Equal(8_000m, copied.GoalAmount); // goal survives the fallback
        Assert.NotEqual(SavingKind.Expenses, copied.Kind);   // the new kind took 4 precisely to leave 3 buried
    }

    [Fact]
    public void An_expenses_fund_round_trips_with_its_costs()
    {
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("Car costs");
        account.SetSavingCosts(car.Id, new[] { new PlannedCost("Insurance", 500m, CostCadence.Quarterly) });
        account.ConfigureSavingExpensesFund(car.Id);

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var copied = copy.SavingCategories.Single(s => s.Name == "Car costs");
        Assert.Equal(SavingKind.Expenses, copied.Kind);
        Assert.True(copied.IsExpensesFund);
        Assert.Single(copied.Costs);
        Assert.Null(copied.GoalAmount);
    }

    [Fact]
    public void A_bucket_that_listed_costs_before_the_kind_existed_adopts_it_on_load()
    {
        // Costs shipped before the kind did, so those buckets are stored as Common. One with costs and no goal was
        // already a sinking fund in all but name; it adopts the kind rather than being offered a goal forever.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("Car costs");
        account.SetSavingCosts(car.Id, new[] { new PlannedCost("Insurance", 500m, CostCadence.Quarterly) });

        var json = AccountSnapshotSerializer.Serialize(account);
        Assert.Contains("\"Kind\":0", json);   // stored as Common, as an older build would have

        var copied = AccountSnapshotSerializer.Deserialize(json).SavingCategories.Single(s => s.Name == "Car costs");
        Assert.Equal(SavingKind.Expenses, copied.Kind);
    }

    [Fact]
    public void A_bucket_with_both_a_goal_and_costs_is_left_as_a_goal_bucket()
    {
        // Genuinely ambiguous, so the loader doesn't pick a side — it keeps the goal, which is the thing the ring
        // and its progress are already drawn from.
        var account = new Account("Home", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var mixed = account.AddSavingCategory("Car");
        account.ConfigureSavingGoal(mixed.Id, 8_000m);
        account.SetSavingCosts(mixed.Id, new[] { new PlannedCost("Insurance", 500m, CostCadence.Quarterly) });

        var copied = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account))
            .SavingCategories.Single(s => s.Name == "Car");

        Assert.Equal(SavingKind.Common, copied.Kind);
        Assert.Equal(8_000m, copied.GoalAmount);
        Assert.Single(copied.Costs);
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
        rent.SetCreatedOn(new DateOnly(2026, 3, 19));   // rent knows when it was set up; electricity is a legacy item

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

        // Losing this on a round-trip would silently undo the "don't back-post" guard, and a legacy item must stay
        // null rather than being stamped with load-time — that would suppress a bill that should genuinely fire.
        Assert.Equal(new DateOnly(2026, 3, 19), rentCopy.CreatedOn);
        Assert.Null(elecCopy.CreatedOn);
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
