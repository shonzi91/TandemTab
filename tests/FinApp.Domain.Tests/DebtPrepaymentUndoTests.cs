using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Savings;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Undoing a loan prepayment.
/// <para>
/// Deploying a savings bucket at a loan does two things: it moves the money out, and it takes the amount off the
/// principal. Undoing it only ever did the first — the cash and the earmark came back while the debt stayed
/// permanently smaller, so the loan reported a balance that no payment had ever produced and the difference could
/// never be recovered through the UI.
/// </para>
/// </summary>
public class DebtPrepaymentUndoTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>An account with a €10,000 payment-driven loan and €3,000 saved toward it.</summary>
    private static (Account Account, SavingCategory Loan, Guid Bank) Setup()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var bank = account.FundId("Bank");

        var loan = account.AddSavingCategory("Car loan");
        account.ConfigureSavingDebt(loan.Id, balance: 10_000m, annualRatePercent: 5m, installment: 300m);
        account.SetSavingDebtPaymentDriven(loan.Id, true, new DateOnly(2026, 1, 1));

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(member.UserId, M(5_000m), fundId: bank);
        p.AllocateToSavings(loan.Id, M(3_000m), new DateOnly(2026, 1, 2));
        return (account, loan, bank);
    }

    [Fact]
    public void Deploying_a_bucket_at_a_loan_takes_the_amount_off_the_principal()
    {
        var (account, loan, bank) = Setup();
        var period = account.CurrentPeriod!;

        period.DisburseSaving(loan.Id, bank, M(2_000m), new DateOnly(2026, 1, 10));
        account.RecordSavingDebtPayment(loan.Id, 2_000m, new DateOnly(2026, 1, 10));

        Assert.Equal(8_000m, loan.DebtBalance);
    }

    /// <summary>★ The bug. Undoing it gave the money back and left the loan €2,000 smaller for good.</summary>
    [Fact]
    public void Undoing_the_prepayment_puts_the_principal_back()
    {
        var (account, loan, bank) = Setup();
        var period = account.CurrentPeriod!;

        var transfer = period.DisburseSaving(loan.Id, bank, M(2_000m), new DateOnly(2026, 1, 10));
        account.RecordSavingDebtPayment(loan.Id, 2_000m, new DateOnly(2026, 1, 10));
        Assert.Equal(8_000m, loan.DebtBalance);

        var drawdown = period.SavingAllocations.Single(a => a.SourceExternalTransferId == transfer.Id);
        period.RemoveSavingMovement(drawdown.Id, loan);

        // The money is back where it was…
        Assert.Equal(3_000m, period.SavingsNetTotal.Amount);
        Assert.DoesNotContain(period.ExternalTransfers, t => t.Id == transfer.Id);
        // …and so is the debt. It stayed at 8,000 before this was fixed.
        Assert.Equal(10_000m, loan.DebtBalance);
    }

    /// <summary>
    /// What the fix actually turns on, and the server's obligation. Omitting the bucket reproduces the old
    /// behaviour exactly — money back, principal not — so this pins that the assertion above is load-bearing
    /// rather than passing for some other reason, and that the caller must pass the bucket for a debt payoff.
    /// </summary>
    [Fact]
    public void Without_the_bucket_the_principal_is_not_restored_which_is_the_old_behaviour()
    {
        var (account, loan, bank) = Setup();
        var period = account.CurrentPeriod!;

        var transfer = period.DisburseSaving(loan.Id, bank, M(2_000m), new DateOnly(2026, 1, 10));
        account.RecordSavingDebtPayment(loan.Id, 2_000m, new DateOnly(2026, 1, 10));

        var drawdown = period.SavingAllocations.Single(a => a.SourceExternalTransferId == transfer.Id);
        period.RemoveSavingMovement(drawdown.Id);   // no bucket

        Assert.Equal(3_000m, period.SavingsNetTotal.Amount);   // the money came back…
        Assert.Equal(8_000m, loan.DebtBalance);                // …and the debt did not. This was the bug.
    }

    /// <summary>The bucket is optional on the call, and omitting it must not throw or half-undo — every movement
    /// that is not a debt payoff passes nothing and expects the money back and no balance touched.</summary>
    [Fact]
    public void Undoing_a_disbursement_from_an_ordinary_bucket_needs_no_bucket_and_touches_no_balance()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var bank = account.FundId("Bank");
        var holiday = account.AddSavingCategory("Holiday");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(2_000m), fundId: bank);
        period.AllocateToSavings(holiday.Id, M(500m), new DateOnly(2026, 1, 2));

        var transfer = period.DisburseSaving(holiday.Id, bank, M(300m), new DateOnly(2026, 1, 10));
        var drawdown = period.SavingAllocations.Single(a => a.SourceExternalTransferId == transfer.Id);

        period.RemoveSavingMovement(drawdown.Id);

        Assert.Equal(500m, period.SavingsNetTotal.Amount);
        Assert.Equal(0m, holiday.DebtBalance);   // never a debt, never touched
    }
}
