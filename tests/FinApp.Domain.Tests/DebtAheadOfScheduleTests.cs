using FinApp.Domain.Accounts;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// "You're already ahead" — the backward-looking figure behind the Home badge.
/// <para>
/// Every other saved-interest number in the app projects FORWARD at the pace you are currently setting aside, and
/// answers "what will this be worth if you keep it up". This one compares where the original schedule said you
/// would be against where you actually are, which is the only basis on which a label may say <i>already</i>.
/// </para>
/// </summary>
public class DebtAheadOfScheduleTests
{
    private static readonly DateOnly Start = new(2025, 1, 1);
    private static readonly DateOnly Now = new(2026, 1, 1);   // twelve installments in

    private static (Account Account, Guid Loan) LoanWith(decimal balanceNow, bool paymentDriven = true)
    {
        var account = new Account("Home", "EUR");
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, balance: balanceNow, annualRatePercent: 6m, installment: 400m,
            originalBalance: 20_000m, startDate: Start);
        if (paymentDriven) account.SetSavingDebtPaymentDriven(loan.Id, true, Start);
        return (account, loan.Id);
    }

    [Fact]
    public void A_loan_running_exactly_to_schedule_is_not_ahead()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var loan = account.FindSavingCategory(loanId)!;
        var scheduled = loan.ScheduledBalanceOn(Now);

        Assert.NotNull(scheduled);
        // Put it exactly where the schedule says it should be, then ask.
        account.ConfigureSavingDebt(loanId, scheduled!.Value, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);

        Assert.Null(account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now));
    }

    [Fact]
    public void A_loan_behind_schedule_is_not_ahead_either()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var scheduled = account.FindSavingCategory(loanId)!.ScheduledBalanceOn(Now)!.Value;

        account.ConfigureSavingDebt(loanId, scheduled + 1_000m, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);

        Assert.Null(account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now));
    }

    [Fact]
    public void Repaying_more_than_the_schedule_asked_is_months_and_interest_already_banked()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var scheduled = account.FindSavingCategory(loanId)!.ScheduledBalanceOn(Now)!.Value;

        // Three thousand more repaid than the installments alone would have managed.
        account.ConfigureSavingDebt(loanId, scheduled - 3_000m, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);

        var ahead = account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now);

        Assert.NotNull(ahead);
        Assert.True(ahead!.Value.MonthsAhead > 0, "repaying 3,000 early must take months off the remaining term");
        Assert.True(ahead.Value.InterestSaved > 0m, "…and the interest those months would have charged");
    }

    /// <summary>Without an origination date there is no schedule to compare against, and inventing one would
    /// manufacture progress out of nothing.</summary>
    [Fact]
    public void A_loan_with_no_start_date_has_nothing_to_be_ahead_of()
    {
        var account = new Account("Home", "EUR");
        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, balance: 9_000m, annualRatePercent: 6m, installment: 400m,
            originalBalance: 20_000m);
        account.SetSavingDebtPaymentDriven(loan.Id, true, Start);

        Assert.Null(loan.ScheduledBalanceOn(Now));
        Assert.Null(loan.AheadOfScheduleOn(Now));
    }

    [Fact]
    public void An_ordinary_savings_bucket_is_never_ahead_of_a_schedule_it_does_not_have()
    {
        var account = new Account("Home", "EUR");
        var holiday = account.AddSavingCategory("Holiday");

        Assert.Null(holiday.ScheduledBalanceOn(Now));
        Assert.Null(holiday.AheadOfScheduleOn(Now));
    }
}
