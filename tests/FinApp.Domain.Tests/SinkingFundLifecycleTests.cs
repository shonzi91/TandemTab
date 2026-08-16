using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// The whole life of an <see cref="SavingKind.Expenses"/> bucket — a sinking fund — across the operations a real
/// one actually sees: money put in, the bill paid out of it, that payment corrected, that payment removed, and the
/// month rolling over on top of all of it.
/// <para>
/// These exist because the money model and the <i>display</i> model disagree on purpose (see
/// <see cref="Period.SavingsSetAsideTotal"/>), and a disagreement that is right in one place is exactly the kind
/// that goes wrong in another. Each test pins both sides: what the bucket holds (the money) and what the period
/// reports as set aside (the figure on the card).
/// </para>
/// </summary>
public class SinkingFundLifecycleTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly SavingsReportService Report = new();

    /// <summary>An account with an insurance sinking fund due next June, and an open January period with income.</summary>
    private static (Account Account, SavingCategory Fund, Guid Bills, Guid Bank, Guid Member) Setup(decimal income = 2000m)
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var bank = account.FundId("Bank");
        var bills = account.AddCategory("Bills").Id;

        var fund = account.AddSavingCategory("Car insurance");
        fund.ConfigureExpensesFund();
        fund.ReplaceCosts([new PlannedCost("Annual premium", 600m, CostCadence.OneOff, new DateOnly(2026, 7, 1))]);

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(member.UserId, M(income), fundId: bank);
        return (account, fund, bills, bank, member.UserId);
    }

    private static Money Balance(Account account, SavingCategory fund) =>
        Report.ForBucket(account, account.CurrentPeriod!, fund.Id).AccumulatedTotal;

    // --- Putting money in ------------------------------------------------

    [Fact]
    public void Allocating_into_the_fund_raises_the_balance_and_lowers_what_the_plan_still_asks_for()
    {
        var (account, fund, _, _, _) = Setup();
        var p = account.CurrentPeriod!;
        var asOf = p.From;

        // Six months until the €600 premium: €100 a month, nothing saved yet.
        Assert.Equal(100m, fund.MonthlySetAside(asOf, 0m));
        Assert.Equal(600m, fund.TargetShortfall(0m));

        p.AllocateToSavings(fund.Id, M(300m), new DateOnly(2026, 1, 10));

        Assert.Equal(M(300m), Balance(account, fund));
        Assert.Equal(M(300m), p.SavingsSetAsideTotal);          // money genuinely set aside this period
        Assert.Equal(50m, fund.MonthlySetAside(asOf, 300m));    // half funded → half the monthly ask
        Assert.Equal(300m, fund.TargetShortfall(300m));
    }

    [Fact]
    public void An_over_funded_target_asks_for_nothing_rather_than_going_negative()
    {
        var (account, fund, _, _, _) = Setup();
        var p = account.CurrentPeriod!;
        p.AllocateToSavings(fund.Id, M(900m), new DateOnly(2026, 1, 10));   // more than the €600 premium

        Assert.Equal(0m, fund.MonthlySetAside(p.From, 900m));
        Assert.Equal(0m, fund.TargetShortfall(900m));
    }

    [Fact]
    public void An_overdue_target_asks_for_the_whole_remainder_this_month_and_never_divides_by_zero()
    {
        var (account, fund, _, _, _) = Setup();
        // Read the plan from a month AFTER the premium was due, with a part-funded pot.
        var afterDue = new DateOnly(2026, 9, 1);

        Assert.Equal(600m, fund.MonthlySetAside(afterDue, 0m));    // months-until floors at 1
        Assert.Equal(150m, fund.MonthlySetAside(afterDue, 450m));  // the remainder, not the whole cost
    }

    // --- Paying the bill out of it ---------------------------------------

    [Fact]
    public void Paying_the_bill_drains_the_earmark_but_is_not_counted_as_un_saving()
    {
        var (account, fund, bills, bank, member) = Setup();
        var p = account.CurrentPeriod!;
        p.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));

        p.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 1, 20), member, bank, "Premium");

        Assert.Equal(M(0m), Balance(account, fund));        // the money left the bucket — money model
        Assert.Equal(M(0m), p.SavingsNetTotal);             // and the earmark with it
        Assert.Equal(M(600m), p.SavingsSetAsideTotal);      // but €600 WAS set aside this period; it is not un-saved
        Assert.Equal(M(600m), p.ExpensesTotal);             // and it is real spending
        Assert.Equal(600m, fund.TargetShortfall(0m));       // the plan starts again for next year's premium
    }

    /// <summary>The reported bug's exact shape: the bill lands in a month that put nothing in.</summary>
    [Fact]
    public void Paying_the_bill_in_a_month_that_saved_nothing_reports_zero_set_aside_not_a_negative()
    {
        var (account, fund, bills, bank, member) = Setup();
        var jan = account.CurrentPeriod!;
        jan.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));
        jan.Close();

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(member, M(2000m), fundId: bank);
        Assert.Equal(M(600m), Balance(account, fund));   // the balance carried across the rollover

        feb.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 2, 14), member, bank, "Premium");

        Assert.Equal(M(-600m), feb.SavingsNetTotal);     // the earmark really did drop
        Assert.Equal(M(0m), feb.SavingsSetAsideTotal);   // ...and "set aside this month" floors at nothing saved
        Assert.Equal(0m, Report.PeriodSavingsRate(feb));
        Assert.False(feb.SavingsSetAsideTotal.IsNegative);
    }

    // --- Correcting and undoing the payment ------------------------------

    [Fact]
    public void Editing_the_payment_re_syncs_the_drawdown_instead_of_stacking_a_second_one()
    {
        var (account, fund, bills, bank, member) = Setup();
        var p = account.CurrentPeriod!;
        p.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));
        var paid = p.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 1, 20), member, bank, "Premium");

        // The real premium was €540, not €600.
        var corrected = p.EditExpense(paid.Id, bills, M(540m), bank, "Premium", new DateOnly(2026, 1, 20));

        Assert.Equal(2, p.SavingAllocations.Count);        // the deposit + exactly ONE drawdown, not two
        Assert.Equal(M(60m), Balance(account, fund));      // €600 saved − €540 spent
        Assert.Equal(M(540m), p.ExpensesTotal);
        Assert.Equal(M(600m), p.SavingsSetAsideTotal);     // still the €600 that was genuinely put in
        Assert.Equal(fund.Id, corrected.SourceSavingCategoryId);   // and it is still a savings-funded expense
    }

    [Fact]
    public void Removing_the_payment_restores_the_bucket_exactly()
    {
        var (account, fund, bills, bank, member) = Setup();
        var p = account.CurrentPeriod!;
        p.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));
        var paid = p.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 1, 20), member, bank, "Premium");

        p.RemoveExpense(paid.Id);

        Assert.Single(p.SavingAllocations);               // the drawdown went with it
        Assert.Equal(M(600m), Balance(account, fund));    // back to fully funded
        Assert.Equal(M(0m), p.ExpensesTotal);
        Assert.Equal(M(600m), p.SavingsSetAsideTotal);
        Assert.Equal(0m, fund.TargetShortfall(600m));
    }

    // --- Rolling the month over ------------------------------------------

    [Fact]
    public void The_plan_gets_more_urgent_as_the_due_date_approaches_and_the_balance_carries()
    {
        var (account, fund, _, bank, member) = Setup();
        var jan = account.CurrentPeriod!;
        jan.AllocateToSavings(fund.Id, M(100m), new DateOnly(2026, 1, 10));
        jan.Close();

        // Nothing put in during February — a skipped month.
        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(member, M(2000m), fundId: bank);

        Assert.Equal(M(100m), Balance(account, fund));      // carried, untouched by the rollover
        Assert.Equal(M(0m), feb.SavingsSetAsideTotal);      // and February set nothing aside — honestly zero
        // €500 still to find, now over five months instead of six: the ask goes UP because a month was skipped.
        Assert.Equal(100m, fund.MonthlySetAside(feb.From, 100m));
        Assert.Equal(500m, fund.TargetShortfall(100m));

        feb.Close();
        var mar = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        mar.Deposit(member, M(2000m), fundId: bank);
        Assert.Equal(125m, fund.MonthlySetAside(mar.From, 100m));   // €500 over four months
    }

    [Fact]
    public void Skipping_months_never_lets_the_carried_balance_be_double_counted_as_freshly_saved()
    {
        var (account, fund, _, bank, member) = Setup();
        var jan = account.CurrentPeriod!;
        jan.AllocateToSavings(fund.Id, M(400m), new DateOnly(2026, 1, 10));
        jan.Close();

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(member, M(2000m), fundId: bank);
        feb.AllocateToSavings(fund.Id, M(200m), new DateOnly(2026, 2, 10));

        Assert.Equal(M(600m), Balance(account, fund));       // lifetime balance
        Assert.Equal(M(200m), feb.SavingsSetAsideTotal);     // but only February's own €200 is "saved this month"
        Assert.Equal(0.1m, Report.PeriodSavingsRate(feb));   // 200 / 2000
        Assert.Equal(M(600m), Report.LifetimeSaved(account));
    }

    /// <summary>The earmark is what stops the app offering money it has already promised to the insurer.</summary>
    [Fact]
    public void Money_in_the_fund_is_not_offered_as_free_cash_and_is_released_when_the_bill_is_paid()
    {
        var (account, fund, bills, bank, member) = Setup();
        var p = account.CurrentPeriod!;
        var priorSaved = M(0m);   // first period, nothing carried in

        p.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));
        // €2,000 in, €600 promised to the insurer → €1,400 genuinely free.
        Assert.Equal(M(1400m), p.FreeToAllocateAfter(priorSaved));

        p.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 1, 20), member, bank, "Premium");
        // The money left the account AND the earmark cleared — it must not be subtracted twice.
        Assert.Equal(M(1400m), p.ExpectedClosingBalance);
        Assert.Equal(M(1400m), p.FreeToAllocateAfter(priorSaved));
    }

    /// <summary>A sinking fund is not a goal, so its ring must not claim completion — and a payout mid-plan must
    /// not make the reported set-aside for the whole account go backwards.</summary>
    [Fact]
    public void Lifetime_saved_never_falls_when_a_bucket_is_spent_for_its_purpose()
    {
        var (account, fund, bills, bank, member) = Setup();
        var p = account.CurrentPeriod!;
        p.AllocateToSavings(fund.Id, M(600m), new DateOnly(2026, 1, 10));
        var before = Report.LifetimeSaved(account);

        p.ConvertSavingToExpense(fund.Id, bills, M(600m), new DateOnly(2026, 1, 20), member, bank, "Premium");

        Assert.Equal(before, Report.LifetimeSaved(account));   // deploying a save is not un-saving
        Assert.Equal(M(0m), Report.AccumulatedTotal(account)); // while the money model correctly shows it gone
        Assert.Null(fund.GoalAmount);                          // an expenses fund has nothing to "reach"
    }
}
