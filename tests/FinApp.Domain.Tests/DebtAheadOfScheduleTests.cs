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

        // Sit exactly on the schedule, then make a real prepayment of three thousand — recorded through the same
        // door the app's disburse flow uses, because that recording is now the whole basis of the claim.
        account.ConfigureSavingDebt(loanId, scheduled, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);
        account.RecordSavingDebtPayment(loanId, 3_000m, Now);

        var ahead = account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now);

        Assert.NotNull(ahead);
        Assert.True(ahead!.Value.MonthsAhead > 0, "repaying 3,000 early must take months off the remaining term");
        Assert.True(ahead.Value.InterestSaved > 0m, "…and the interest those months would have charged");
    }

    /// <summary>
    /// ★ The regression this whole rule exists for. A balance lower than the app's reconstruction of the schedule
    /// is <b>not</b> evidence of anything: a real contract differs from a flat amortization for a dozen ordinary
    /// reasons — an arrangement fee, a deferred first month, a rounded original, a lease's residual, a balance
    /// typed a little low — and every one of them used to surface on Home as "2 months ahead, €752 interest saved"
    /// on a loan that had never once been prepaid.
    /// </summary>
    [Fact]
    public void A_balance_below_the_reconstructed_schedule_is_not_a_lead_unless_a_prepayment_was_recorded()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var scheduled = account.FindSavingCategory(loanId)!.ScheduledBalanceOn(Now)!.Value;

        // Three thousand below the model, with nothing recorded to explain it. That is a discrepancy, not a win.
        account.ConfigureSavingDebt(loanId, scheduled - 3_000m, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);

        Assert.Null(account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now));
    }

    /// <summary>The lead may never exceed what the app can point at. A loan sitting below its schedule for unknown
    /// reasons AND carrying one small prepayment is ahead by the prepayment — not by the whole gap.</summary>
    [Fact]
    public void The_lead_is_capped_at_the_principal_actually_recorded()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var scheduled = account.FindSavingCategory(loanId)!.ScheduledBalanceOn(Now)!.Value;

        // 3,000 below the model, of which only 500 is a recorded prepayment.
        account.ConfigureSavingDebt(loanId, scheduled - 2_500m, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);
        account.RecordSavingDebtPayment(loanId, 500m, Now);

        var capped = account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now);

        // …and the same loan with the full 3,000 recorded is ahead by more. If the cap were not applied the two
        // would be identical, because the balance gap is the same in both.
        var (other, otherId) = LoanWith(balanceNow: 0m);
        var otherScheduled = other.FindSavingCategory(otherId)!.ScheduledBalanceOn(Now)!.Value;
        other.ConfigureSavingDebt(otherId, otherScheduled, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        other.SetSavingDebtPaymentDriven(otherId, true, Start);
        other.RecordSavingDebtPayment(otherId, 3_000m, Now);
        var full = other.FindSavingCategory(otherId)!.AheadOfScheduleOn(Now);

        Assert.NotNull(capped);
        Assert.NotNull(full);
        Assert.True(full!.Value.InterestSaved > capped!.Value.InterestSaved,
            "a 3,000 prepayment must bank more than a 500 one, however wide the unexplained gap is");
    }

    /// <summary>Undoing a prepayment gives back the lead it bought, not just the money — the loan must stop
    /// claiming a head start it no longer has.</summary>
    [Fact]
    public void Undoing_the_prepayment_takes_the_lead_back_with_it()
    {
        var (account, loanId) = LoanWith(balanceNow: 0m);
        var loan = account.FindSavingCategory(loanId)!;
        var scheduled = loan.ScheduledBalanceOn(Now)!.Value;

        account.ConfigureSavingDebt(loanId, scheduled, 6m, 400m, originalBalance: 20_000m, startDate: Start);
        account.SetSavingDebtPaymentDriven(loanId, true, Start);
        account.RecordSavingDebtPayment(loanId, 3_000m, Now);
        Assert.NotNull(account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now));

        account.FindSavingCategory(loanId)!.ReverseDebtPayment(3_000m, Now, isExtraRepayment: true);

        Assert.Null(account.FindSavingCategory(loanId)!.AheadOfScheduleOn(Now));
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
