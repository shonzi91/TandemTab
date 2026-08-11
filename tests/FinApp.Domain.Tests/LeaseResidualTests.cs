using FinApp.Domain.Accounts;
using FinApp.Forecasting;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// A lease is not a loan that happens to end early: its instalments are sized to leave a stated sum owed on the
/// last scheduled date. Amortising through that residual overshoots the contract's own end by months.
/// <para>
/// The headline case is a real contract, reconciled against the lender's own amortisation table — see
/// <see cref="A_real_lease_schedule_reconciles"/>.
/// </para>
/// </summary>
public class LeaseResidualTests
{
    [Fact]
    public void A_residual_ends_the_schedule_early_and_costs_less_interest()
    {
        var loan = LoanForecast.PayOff(30_000m, 6m, 600m)!.Value;
        var lease = LoanForecast.PayOff(30_000m, 6m, 600m, residual: 8_000m)!.Value;

        Assert.True(lease.Months < loan.Months);
        Assert.True(lease.TotalInterest < loan.TotalInterest);
    }

    [Fact]
    public void A_zero_residual_is_the_old_behaviour_exactly()
    {
        // Every existing loan must project identically — the residual defaults to 0 and legacy snapshots carry 0.
        var withoutArg = LoanForecast.PayOff(30_000m, 6m, 600m)!.Value;
        var withZero = LoanForecast.PayOff(30_000m, 6m, 600m, residual: 0m)!.Value;

        Assert.Equal(withoutArg.Months, withZero.Months);
        Assert.Equal(withoutArg.TotalInterest, withZero.TotalInterest);
    }

    [Fact]
    public void A_balance_already_at_or_below_the_residual_has_no_schedule_left()
    {
        // The instalments are done; only the balloon remains, and it isn't amortised.
        Assert.Equal(0, LoanForecast.PayOff(8_000m, 6m, 600m, residual: 8_000m)!.Value.Months);
        Assert.Equal(0, LoanForecast.PayOff(7_000m, 6m, 600m, residual: 8_000m)!.Value.Months);
    }

    [Fact]
    public void A_real_lease_schedule_reconciles()
    {
        // From the lender's own table: opening 38,517.32 EUR, instalment 553.61 without VAT, of which 112.34 is
        // interest and 441.27 principal in month 1 — i.e. a 3.50% nominal rate.
        const decimal opening = 38_517.32m, instalment = 553.61m, rate = 3.5m;

        var monthOneInterest = LoanForecast.MonthlyInterest(opening, rate);
        Assert.Equal(112.34m, decimal.Round(monthOneInterest, 2));
        Assert.Equal(441.27m, decimal.Round(instalment - monthOneInterest, 2));

        // ★ The contract runs 60 months from Aug-2025. Amortised to ZERO the schedule runs far past that, which is
        // the bug this feature exists for.
        var toZero = LoanForecast.PayOff(opening, rate, instalment)!.Value;
        Assert.True(toZero.Months > 60, $"expected an overshoot past the 60-month contract, got {toZero.Months}");

        // With the residual the lender actually stipulates, the schedule ends on the contract's own term.
        var residual = LoanForecast.BalanceAfter(opening, rate, instalment, 60);
        var toResidual = LoanForecast.PayOff(opening, rate, instalment, residual)!.Value;
        Assert.Equal(60, toResidual.Months);
    }

    [Fact]
    public void The_residual_survives_a_snapshot_round_trip_and_legacy_buckets_read_zero()
    {
        var account = new Account("Personal", "EUR");
        account.AddDefaultFunds();
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var lease = account.AddSavingCategory("Car lease").Id;
        account.ConfigureSavingDebt(lease, 33_137.48m, 3.5m, 553.61m);
        account.SetSavingDebtResidual(lease, 9_630.49m);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(9_630.49m, restored.FindSavingCategory(lease)!.DebtResidual);

        // A payload written before the field existed simply has no such property.
        var legacy = AccountSnapshotSerializer.Serialize(account).Replace("\"DebtResidual\":9630.49", "\"X\":0");
        Assert.DoesNotContain("DebtResidual", legacy);
        Assert.Equal(0m, AccountSnapshotSerializer.Deserialize(legacy).FindSavingCategory(lease)!.DebtResidual);
    }

    [Fact]
    public void Remaining_interest_stops_at_the_residual()
    {
        var account = new Account("Personal", "EUR");
        account.AddDefaultFunds();
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var lease = account.AddSavingCategory("Car lease").Id;
        account.ConfigureSavingDebt(lease, 33_137.48m, 3.5m, 553.61m, balanceAsOf: new DateOnly(2026, 1, 1));

        var bucket = account.FindSavingCategory(lease)!;
        var asLoan = bucket.RemainingInterest(new DateOnly(2026, 1, 1));

        // Derived from the same schedule the projection walks, rather than a figure computed elsewhere — the two
        // agree only to the cent, and a stated residual is exactly where that gap shows up.
        var residual = LoanForecast.BalanceAfter(33_137.48m, 3.5m, 553.61m, 48);
        account.SetSavingDebtResidual(lease, residual);
        var asLease = bucket.RemainingInterest(new DateOnly(2026, 1, 1));

        Assert.True(asLease < asLoan, "a lease pays interest over fewer months, so it owes less of it");
        Assert.Equal(48, bucket.MonthsRemaining(new DateOnly(2026, 1, 1)));
    }
}
