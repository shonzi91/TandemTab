using FinApp.Domain.Accounts;
using FinApp.Domain.Savings;
using FinApp.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// The schedule-driven balance walk lands on the loan's <b>stated due day</b>, and says so.
/// <para>
/// The bug these pin: the walk used to count from the anchor's own day-of-month, i.e. whichever day somebody
/// happened to type the balance on. A loan due on the 5th, stated on the 18th, did not move on the 5th — the due
/// day passed and the figure sat still for another thirteen days, which reads as a missed payment. The due day is a
/// fact about the contract; the anchor date is an accident of data entry.
/// </para>
/// </summary>
public class DebtScheduleDayTests
{
    private const string Eur = "EUR";

    /// <summary>A €12,000 loan at 6% paying €300, balance stated mid-month, due on the day given.</summary>
    private static SavingCategory Loan(int? dueDay, DateOnly statedOn, decimal balance = 12_000m)
    {
        var account = new Account("Home", Eur);
        var bucket = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(bucket.Id, balance, 6m, 300m, balanceAsOf: statedOn, installmentDay: dueDay);
        return account.FindSavingCategory(bucket.Id)!;
    }

    // --- The walk counts due days ---------------------------------------------------------------------

    [Fact]
    public void The_balance_moves_on_the_due_day_not_on_the_day_it_was_typed()
    {
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));
        var oneInstallment = LoanForecast.BalanceAfter(12_000m, 6m, 300m, 1);

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 9, 4)));    // the day before it's due
        Assert.Equal(oneInstallment, loan.DebtBalanceOn(new DateOnly(2026, 9, 5)));   // the due day itself
        // …and it does NOT wait for the 18th, which is all the old rule ever did.
        Assert.Equal(oneInstallment, loan.DebtBalanceOn(new DateOnly(2026, 9, 17)));
    }

    [Fact]
    public void A_due_day_that_has_not_come_round_since_the_balance_was_stated_counts_nothing()
    {
        // Stated on the 18th, due on the 5th: August's payment is already reflected in the figure that was typed,
        // so nothing comes off until September's.
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public void A_due_day_still_ahead_in_the_anchor_month_counts_that_month()
    {
        // Stated on the 1st, due on the 5th: this month's payment has NOT happened yet, so it must come off.
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 1));

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 8, 4)));
        Assert.Equal(LoanForecast.BalanceAfter(12_000m, 6m, 300m, 1), loan.DebtBalanceOn(new DateOnly(2026, 8, 5)));
    }

    [Fact]
    public void The_anchor_days_own_installment_is_never_counted_twice()
    {
        // Stated ON the due day: that payment is in the number, so the next one is a month away.
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 5));

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 9, 4)));
        Assert.Equal(LoanForecast.BalanceAfter(12_000m, 6m, 300m, 1), loan.DebtBalanceOn(new DateOnly(2026, 9, 5)));
    }

    [Fact]
    public void A_thirty_first_due_day_falls_on_the_last_day_of_a_short_month()
    {
        // February has no 31st. The payment lands on the 28th rather than slipping into March.
        var loan = Loan(dueDay: 31, statedOn: new DateOnly(2026, 1, 31));

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 2, 27)));
        Assert.Equal(LoanForecast.BalanceAfter(12_000m, 6m, 300m, 1), loan.DebtBalanceOn(new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void A_year_of_due_days_is_twelve_installments()
    {
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));

        Assert.Equal(LoanForecast.BalanceAfter(12_000m, 6m, 300m, 12), loan.DebtBalanceOn(new DateOnly(2027, 8, 5)));
    }

    [Fact]
    public void With_no_stated_due_day_the_old_anchor_rule_still_applies()
    {
        // Unchanged behaviour for every bucket saved before a due day was recorded — the fallback is the point.
        var loan = Loan(dueDay: null, statedOn: new DateOnly(2026, 8, 18));

        Assert.Equal(12_000m, loan.DebtBalanceOn(new DateOnly(2026, 9, 17)));
        Assert.Equal(LoanForecast.BalanceAfter(12_000m, 6m, 300m, 1), loan.DebtBalanceOn(new DateOnly(2026, 9, 18)));
    }

    // --- What the row reports -------------------------------------------------------------------------

    [Fact]
    public void Before_the_first_due_day_the_step_names_the_date_the_balance_moves()
    {
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));

        var step = loan.ScheduleStepOn(new DateOnly(2026, 8, 20));

        Assert.NotNull(step);
        Assert.Null(step!.LastOn);                                  // nothing has come off yet
        Assert.Equal(new DateOnly(2026, 9, 5), step.NextOn);
    }

    [Fact]
    public void After_a_due_day_the_step_reports_what_came_off_and_when_the_next_one_lands()
    {
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));

        var step = loan.ScheduleStepOn(new DateOnly(2026, 9, 10));

        Assert.NotNull(step);
        Assert.Equal(new DateOnly(2026, 9, 5), step!.LastOn);
        Assert.Equal(new DateOnly(2026, 10, 5), step.NextOn);
        // Split by the same rule the ledger posts: this month's interest on what was owed, principal is the rest.
        var interest = LoanForecast.MonthlyInterest(12_000m, 6m);
        Assert.Equal(interest, step.LastInterest);
        Assert.Equal(300m - interest, step.LastPrincipal);
        Assert.Equal(300m, step.LastPrincipal + step.LastInterest);
    }

    [Fact]
    public void A_payment_driven_loan_has_no_schedule_step_to_report()
    {
        // Nothing for the schedule to say: the balance is waiting on a logged payment, which is what that mode's
        // own flag reports instead.
        var account = new Account("Home", Eur);
        var bucket = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(bucket.Id, 12_000m, 6m, 300m,
            balanceAsOf: new DateOnly(2026, 8, 18), installmentDay: 5);
        account.SetSavingDebtPaymentDriven(bucket.Id, true, new DateOnly(2026, 8, 18));

        Assert.Null(account.FindSavingCategory(bucket.Id)!.ScheduleStepOn(new DateOnly(2026, 9, 10)));
    }

    [Fact]
    public void The_step_is_dated_by_the_same_counter_that_moves_the_balance()
    {
        // The guard that matters: a date derived independently of the walk could name a day the figure doesn't
        // change on. Walk the whole year and assert the balance moves on exactly the days the step promises.
        var loan = Loan(dueDay: 5, statedOn: new DateOnly(2026, 8, 18));
        var day = new DateOnly(2026, 8, 18);

        for (var i = 0; i < 365; i++, day = day.AddDays(1))
        {
            var step = loan.ScheduleStepOn(day);
            Assert.NotNull(step);
            var owed = loan.DebtBalanceOn(day);
            var owedTheDayBefore = loan.DebtBalanceOn(day.AddDays(-1));

            if (owed != owedTheDayBefore)
                Assert.Equal(day, step!.LastOn);      // it moved today, so today is the last installment
            Assert.True(step!.NextOn > day, $"next installment {step.NextOn} should be after {day}");
        }
    }
}
