using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class SavingsTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Accumulates_savings_across_periods()
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var vacations = account.AddSavingCategory("Vacations");

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(member.UserId, M(100));
        p1.AllocateToSavings(vacations.Id, M(100), new DateOnly(2026, 1, 15));
        p1.Close();

        var p2 = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p2.Deposit(member.UserId, M(150));
        p2.AllocateToSavings(vacations.Id, M(150), new DateOnly(2026, 2, 15));

        var report = new SavingsReportService().ForBucket(account, p2, vacations.Id);

        Assert.Equal(M(250), report.AccumulatedTotal); // 100 + 150 across both periods
        Assert.Equal(M(150), report.PeriodNet);        // only p2's movement
    }

    [Fact]
    public void Average_deposit_pace_is_per_active_period_and_ignores_empty_periods()
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var car = account.AddSavingCategory("Car loan");
        var svc = new SavingsReportService();

        // No deposits yet → no pace.
        Assert.Null(svc.AverageDepositPace(account, car.Id));

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(member.UserId, M(500));
        p1.AllocateToSavings(car.Id, M(200), new DateOnly(2026, 1, 10));
        p1.Close();

        // A period with no deposit into this bucket — must not drag the average down.
        var p2 = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p2.Close();

        var p3 = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        p3.Deposit(member.UserId, M(500));
        p3.AllocateToSavings(car.Id, M(300), new DateOnly(2026, 3, 10));

        // Average over the two active periods: (200 + 300) / 2 = 250.
        Assert.Equal(M(250), svc.AverageDepositPace(account, car.Id));
    }

    [Fact]
    public void Debt_payment_lowers_remaining_owed_and_floors_at_zero()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var car = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(car.Id, balance: 1000m, annualRatePercent: 5m, installment: 100m);

        account.RecordSavingDebtPayment(car.Id, 300m);
        Assert.Equal(700m, account.FindSavingCategory(car.Id)!.DebtBalance);
        Assert.False(account.FindSavingCategory(car.Id)!.IsDebtCleared);

        account.RecordSavingDebtPayment(car.Id, 5000m);   // overpay → floors at zero, now cleared
        Assert.Equal(0m, account.FindSavingCategory(car.Id)!.DebtBalance);
        Assert.True(account.FindSavingCategory(car.Id)!.IsDebtCleared);
    }

    [Fact]
    public void Original_debt_balance_is_captured_and_survives_payments_for_progress()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, balance: 10000m, annualRatePercent: 5m, installment: 200m);

        var bucket = account.FindSavingCategory(loan.Id)!;
        Assert.Equal(10000m, bucket.DebtOriginalBalance);   // captured on first config
        Assert.Equal(0m, bucket.DebtPaidOff);
        Assert.Equal(0m, bucket.DebtProgressRatio);

        account.RecordSavingDebtPayment(loan.Id, 2500m);
        Assert.Equal(7500m, bucket.DebtBalance);
        Assert.Equal(10000m, bucket.DebtOriginalBalance);   // original stays put
        Assert.Equal(2500m, bucket.DebtPaidOff);
        Assert.Equal(0.25m, bucket.DebtProgressRatio);
    }

    [Fact]
    public void Editing_debt_preserves_original_balance_but_grows_it_when_balance_is_corrected_upward()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var loan = account.AddSavingCategory("Loan");
        account.ConfigureSavingDebt(loan.Id, balance: 10000m, annualRatePercent: 5m, installment: 200m);
        account.RecordSavingDebtPayment(loan.Id, 4000m);   // remaining 6000, original 10000

        // Re-configuring with the remaining balance (the edit modal pre-fills what's owed) keeps the original.
        account.ConfigureSavingDebt(loan.Id, balance: 6000m, annualRatePercent: 4m, installment: 250m);
        Assert.Equal(10000m, account.FindSavingCategory(loan.Id)!.DebtOriginalBalance);

        // Correcting the balance above the original (borrowed more) grows the original to match.
        account.ConfigureSavingDebt(loan.Id, balance: 12000m, annualRatePercent: 4m, installment: 250m);
        Assert.Equal(12000m, account.FindSavingCategory(loan.Id)!.DebtOriginalBalance);
    }

    [Fact]
    public void Common_bucket_has_no_debt_progress()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var vac = account.AddSavingCategory("Vacations");
        var bucket = account.FindSavingCategory(vac.Id)!;
        Assert.Equal(0m, bucket.DebtPaidOff);
        Assert.Null(bucket.DebtProgressRatio);
    }

    [Fact]
    public void Planned_contribution_is_set_and_cleared()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var vac = account.AddSavingCategory("Vacations");

        account.SetSavingPlannedContribution(vac.Id, 300m);
        Assert.Equal(300m, account.FindSavingCategory(vac.Id)!.PlannedContribution);

        account.SetSavingPlannedContribution(vac.Id, 0m);   // zero clears it
        Assert.Null(account.FindSavingCategory(vac.Id)!.PlannedContribution);

        account.SetSavingPlannedContribution(vac.Id, 150m);
        account.SetSavingPlannedContribution(vac.Id, null);  // null clears it
        Assert.Null(account.FindSavingCategory(vac.Id)!.PlannedContribution);

        Assert.Throws<ArgumentException>(() => account.SetSavingPlannedContribution(vac.Id, -5m));
    }

    [Fact]
    public void Debt_balance_history_shrinks_with_each_paying_period()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var fund = account.FundId("Bank");
        var loan = account.AddSavingCategory("Loan");
        account.ConfigureSavingDebt(loan.Id, balance: 1000m, annualRatePercent: 5m, installment: 100m);
        var svc = new SavingsReportService();

        // No payments yet → just the original balance, so nothing to draw.
        Assert.Equal(new[] { 1000m }, svc.DebtBalanceHistory(account, loan.Id));

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(member.UserId, M(1000), fundId: fund);
        p1.AllocateToSavings(loan.Id, M(300), new DateOnly(2026, 1, 5));
        p1.DisburseSaving(loan.Id, fund, M(300), new DateOnly(2026, 1, 20), "payment");
        account.RecordSavingDebtPayment(loan.Id, 300m);
        p1.Close();

        var p2 = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p2.Deposit(member.UserId, M(1000), fundId: fund);
        p2.AllocateToSavings(loan.Id, M(200), new DateOnly(2026, 2, 5));
        p2.DisburseSaving(loan.Id, fund, M(200), new DateOnly(2026, 2, 20), "payment");
        account.RecordSavingDebtPayment(loan.Id, 200m);

        // Original 1000 → 700 after p1's 300 payment → 500 after p2's 200 payment.
        Assert.Equal(new[] { 1000m, 700m, 500m }, svc.DebtBalanceHistory(account, loan.Id));
        Assert.Equal(500m, account.FindSavingCategory(loan.Id)!.DebtBalance);
        Assert.Equal(500m, account.FindSavingCategory(loan.Id)!.DebtPaidOff);
    }

    [Fact]
    public void Clearing_debt_resets_original_balance()
    {
        var account = new Account("Home", Eur);
        account.AssignOwner(Guid.NewGuid(), "Me");
        var loan = account.AddSavingCategory("Loan");
        account.ConfigureSavingDebt(loan.Id, balance: 5000m, annualRatePercent: 3m, installment: 100m);
        account.ClearSavingDebt(loan.Id);
        Assert.Equal(0m, account.FindSavingCategory(loan.Id)!.DebtOriginalBalance);
    }

    [Fact]
    public void Converting_saving_to_expense_draws_down_bucket_and_records_expense()
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var vacations = account.AddSavingCategory("Vacations");
        var travel = account.AddCategory("Vacations");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(300));
        period.AllocateToSavings(vacations.Id, M(300), new DateOnly(2026, 1, 5));

        // Actually spend 120 of the vacation savings on a real expense.
        var expense = period.ConvertSavingToExpense(
            vacations.Id, travel.Id, M(120), new DateOnly(2026, 1, 20), member.UserId, Guid.NewGuid(), "Flights");

        Assert.True(expense.IsFromSavings);
        Assert.Equal(M(120), period.ExpensesTotal);          // physical money left the account
        Assert.Equal(M(180), period.SavingsNetTotal);        // 300 allocated - 120 drawn down

        var report = new SavingsReportService().ForBucket(account, period, vacations.Id);
        Assert.Equal(M(180), report.AccumulatedTotal);
    }

    [Fact]
    public void Period_savings_rate_is_net_savings_over_paid_contributions()
    {
        var account = new Account("Personal", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var others = account.AddSavingCategory("Others");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000));
        period.AllocateToSavings(others.Id, M(200), new DateOnly(2026, 1, 10));

        Assert.Equal(0.2m, new SavingsReportService().PeriodSavingsRate(period));
    }

    [Fact]
    public void Disbursing_a_saving_to_a_goal_is_not_an_expense_and_keeps_the_savings_rate()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var fund = account.FundId("Bank");
        var loan = account.AddSavingCategory("Loan payoff");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(member.UserId, M(1000), fundId: fund);
        p.AllocateToSavings(loan.Id, M(200), new DateOnly(2026, 1, 5));
        var rateBefore = new SavingsReportService().PeriodSavingsRate(p);   // 200 / 1000 = 0.2

        // Deploy the whole bucket to the loan — money leaves the account, but it isn't consumption.
        p.DisburseSaving(loan.Id, fund, M(200), new DateOnly(2026, 1, 20), "Loan prepayment");

        Assert.Equal(M(0), p.ExpensesTotal);          // not an expense
        Assert.Equal(M(200), p.ExternalOutTotal);     // money genuinely left the account
        Assert.Equal(M(0), p.SavingsNetTotal);        // earmark (money model) drained — money left
        Assert.Equal(M(200), p.SavingsSetAsideTotal); // but "saved this period" stays — deploying isn't un-saving
        Assert.Equal(rateBefore, new SavingsReportService().PeriodSavingsRate(p));   // rate unchanged
        Assert.Equal(M(200), new SavingsReportService().LifetimeSaved(account));     // total saved stays too
        Assert.Contains(p.SavingMovements(), m => m.IsDisbursement);                 // listed as a savings movement
    }

    [Fact]
    public void Removing_a_disbursement_transfer_restores_the_bucket()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var fund = account.FundId("Bank");
        var loan = account.AddSavingCategory("Loan payoff");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(member.UserId, M(1000), fundId: fund);
        p.AllocateToSavings(loan.Id, M(200), new DateOnly(2026, 1, 5));
        var xfer = p.DisburseSaving(loan.Id, fund, M(200), new DateOnly(2026, 1, 20), "Loan prepayment");
        Assert.Equal(M(0), p.SavingsNetTotal);

        p.RemoveExternalTransfer(xfer.Id);

        Assert.Equal(M(200), p.SavingsNetTotal);   // the paired drawdown is gone → the bucket is whole again
        Assert.Equal(M(0), p.ExternalOutTotal);
    }

    [Fact]
    public void A_savings_deposit_can_be_edited_and_removed()
    {
        var account = new Account("Home", Eur);
        var bucket = account.AddSavingCategory("Vacations");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000));
        var deposit = period.AllocateToSavings(bucket.Id, M(300), new DateOnly(2026, 1, 5));

        Assert.Equal(deposit.Id, Assert.Single(period.ManualSavingDeposits()).Id);

        period.EditSavingDeposit(deposit.Id, M(450));
        Assert.Equal(M(450), period.SavingsNetTotal);

        period.RemoveSavingAllocation(Assert.Single(period.ManualSavingDeposits()).Id);
        Assert.Equal(M(0), period.SavingsNetTotal);
        Assert.Empty(period.ManualSavingDeposits());
    }

    [Fact]
    public void Editing_a_savings_deposit_past_the_cash_is_advisory_not_blocked()
    {
        var account = new Account("Home", Eur);
        var bucket = account.AddSavingCategory("Vacations");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(500));
        var deposit = period.AllocateToSavings(bucket.Id, M(200), new DateOnly(2026, 1, 5));

        // Raising the deposit beyond the contributed cash is allowed now; it just drives free-to-allocate negative.
        period.EditSavingDeposit(deposit.Id, M(600));
        Assert.Equal(M(600), period.SavingsNetTotal);
        Assert.True(period.FreeToAllocateAfter(M(0)).IsNegative);
    }

    [Fact]
    public void Saving_conversion_adds_to_a_budget()
    {
        var account = new Account("Home", Eur);
        var member = account.AddMember(Guid.NewGuid(), "A");
        var food = account.AddCategory("Food");
        var bucket = account.AddSavingCategory("Reserve");

        var p1 = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p1.Deposit(member.UserId, M(1000));
        p1.AllocateToSavings(bucket.Id, M(300), new DateOnly(2026, 1, 5));
        p1.Close();

        var p2 = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p2.Deposit(member.UserId, M(500));
        p2.SetBudget(food.Id, M(500));

        p2.ConvertSavingToBudget(bucket.Id, food.Id, M(200), new DateOnly(2026, 2, 10)); // matures saving into the budget
        Assert.Equal(M(700), p2.FindBudget(food.Id)!.Allocated);
    }

    [Fact]
    public void Transfer_and_drawdown_allocations_are_not_treated_as_deposits()
    {
        var account = new Account("Home", Eur);
        var a = account.AddSavingCategory("A");
        var b = account.AddSavingCategory("B");
        var travel = account.AddCategory("Travel");
        var member = account.AddMember(Guid.NewGuid(), "M");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000));
        period.AllocateToSavings(a.Id, M(400), new DateOnly(2026, 1, 2));      // the one real deposit
        period.TransferSavings(a.Id, b.Id, M(100), new DateOnly(2026, 1, 3));  // two noted halves
        period.ConvertSavingToExpense(a.Id, travel.Id, M(50), new DateOnly(2026, 1, 4), member.UserId, Guid.NewGuid()); // linked drawdown

        Assert.Single(period.ManualSavingDeposits()); // only the AllocateToSavings deposit qualifies
    }

    [Fact]
    public void Looking_back_at_a_closed_period_shows_what_the_bucket_held_then()
    {
        // The bug: the accumulated total summed EVERY period regardless of which one you were viewing, so
        // navigating back to January showed today's balance — a number January's own movements can't add up to.
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var car = account.AddSavingCategory("Car");

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.Deposit(member.UserId, M(100));
        jan.AllocateToSavings(car.Id, M(100), new DateOnly(2026, 1, 15));
        jan.Close();

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(member.UserId, M(250));
        feb.AllocateToSavings(car.Id, M(250), new DateOnly(2026, 2, 15));

        var svc = new SavingsReportService();

        // Viewing January: February's 250 hadn't happened yet.
        Assert.Equal(M(100), svc.ForBucket(account, jan, car.Id).AccumulatedTotal);
        // Viewing February: both count.
        Assert.Equal(M(350), svc.ForBucket(account, feb, car.Id).AccumulatedTotal);
        // The all-time total is unchanged — it deliberately has no "as of".
        Assert.Equal(M(350), svc.AccumulatedTotal(account));
    }
}
