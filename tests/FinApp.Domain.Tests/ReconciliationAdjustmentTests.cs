using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Reconciliation adjustments are corrections written into the period a rollover CLOSES. They were previously
/// indistinguishable from ordinary entries, which meant nothing could undo or resize them: removing the period
/// they justified left them behind, and editing the opening balance they were derived from left them stale.
/// <para>These tests pin the stamp that makes both possible, and the arithmetic that keeps a resize honest.</para>
/// </summary>
public class ReconciliationAdjustmentTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>An account with one fund, one closed-able period holding a known ledger balance, and a member.</summary>
    private static (Account Account, Guid FundId, Guid MemberId) Seed(decimal opening)
    {
        var account = new Account("Test", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Tester");
        var fund = account.AddFund("Bank");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(fund.Id, M(opening));
        return (account, fund.Id, member.Id);
    }

    [Fact]
    public void A_negative_drift_becomes_a_stamped_expense()
    {
        // Ledger says 100, reality says 80 — money left without being logged.
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];

        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);

        var expense = Assert.Single(period.Expenses);
        Assert.Equal(20m, expense.Amount.Amount);
        Assert.Equal(period.Id, expense.ReconciliationForPeriodId);
        Assert.Equal(80m, period.FundBalance(fundId).Amount);
    }

    [Fact]
    public void A_positive_drift_becomes_a_stamped_deposit()
    {
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];

        account.RecordReconciliationAdjustment(period, fundId, 15m, period.To, memberId);

        var deposit = Assert.Single(period.Contributions);
        Assert.Equal(15m, deposit.Paid.Amount);
        Assert.Equal(period.Id, deposit.ReconciliationForPeriodId);
        Assert.Equal(115m, period.FundBalance(fundId).Amount);
    }

    // ── Bug 1: removing the period must take its adjustments with it ──────────────────────────────────────────

    [Fact]
    public void Removing_the_period_removes_the_adjustments_that_rollover_wrote()
    {
        // ⭐ The reported bug. The adjustment lives in the PREVIOUS period, so "delete this period and everything
        // in it" left it standing — a correction for a rollover that no longer existed.
        var (account, fundId, memberId) = Seed(100m);
        var closing = account.Periods[0];
        account.RecordReconciliationAdjustment(closing, fundId, -20m, closing.To, memberId);
        closing.Close();
        account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));

        account.RemoveLatestPeriod();

        var reopened = Assert.Single(account.Periods);
        Assert.Empty(reopened.Expenses);
        Assert.Equal(100m, reopened.FundBalance(fundId).Amount);   // back to what the books said before
    }

    [Fact]
    public void Removing_the_period_leaves_ordinary_entries_alone()
    {
        // ⚠️ The guard on the above: only STAMPED entries go. A real expense in the reopened period is not a
        // rollover artefact and deleting it would destroy data.
        var (account, fundId, memberId) = Seed(100m);
        var closing = account.Periods[0];
        closing.AddExpense(new FinApp.Domain.Budgeting.Expense(
            account.AddCategory("Food").Id, M(10), closing.To, memberId, fundId));
        account.RecordReconciliationAdjustment(closing, fundId, -20m, closing.To, memberId);
        closing.Close();
        account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));

        account.RemoveLatestPeriod();

        var expense = Assert.Single(account.Periods[0].Expenses);
        Assert.Equal(10m, expense.Amount.Amount);
        Assert.Null(expense.ReconciliationForPeriodId);
    }

    // ── Bug 2: editing the opening must resize the adjustment ─────────────────────────────────────────────────

    [Fact]
    public void The_drift_excludes_the_adjustment_already_booked()
    {
        // ⚠️⚠️ The trap that would make every edit shrink the adjustment towards zero. Once -20 is booked the
        // fund reads 80, which IS the entered figure — so a naive (entered − balance) recompute would say the
        // drift is now nothing.
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];
        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);

        Assert.Equal(80m, period.FundBalance(fundId).Amount);
        Assert.Equal(-20m, period.ReconciliationDriftFor(fundId, 80m));   // unchanged entry → unchanged drift
        Assert.Equal(-30m, period.ReconciliationDriftFor(fundId, 70m));   // entered less → bigger drift
        Assert.Equal(0m, period.ReconciliationDriftFor(fundId, 100m));    // entered matches the ledger → none
    }

    [Fact]
    public void Resizing_keeps_the_category_and_the_note()
    {
        // ★ The owner's call: only the amount is arithmetic's business. Someone who recategorised or explained a
        // drift keeps that.
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];
        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);
        var original = period.Expenses[0];
        var ownCategory = account.AddCategory("Cash withdrawal");
        period.EditExpense(original.Id, ownCategory.Id, M(20m), fundId, "I took cash out", period.To);

        Assert.True(period.ResizeReconciliationAdjustment(fundId, -35m));

        var resized = Assert.Single(period.Expenses);
        Assert.Equal(35m, resized.Amount.Amount);
        Assert.Equal(ownCategory.Id, resized.CategoryId);          // kept
        Assert.Equal("I took cash out", resized.Note);             // kept
        Assert.Equal(period.Id, resized.ReconciliationForPeriodId); // and still undoable
    }

    [Fact]
    public void A_drift_that_disappears_removes_the_adjustment()
    {
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];
        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);

        Assert.True(period.ResizeReconciliationAdjustment(fundId, 0m));

        Assert.Empty(period.Expenses);
        Assert.Equal(100m, period.FundBalance(fundId).Amount);
    }

    [Fact]
    public void A_drift_that_changes_sign_asks_the_caller_to_write_the_other_kind()
    {
        // An expense cannot be reshaped into a deposit, so the old one is cleared and false says "your turn".
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];
        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);

        Assert.False(period.ResizeReconciliationAdjustment(fundId, 15m));
        Assert.Empty(period.Expenses);

        account.RecordReconciliationAdjustment(period, fundId, 15m, period.To, memberId);
        Assert.Equal(15m, Assert.Single(period.Contributions).Paid.Amount);
    }

    [Fact]
    public void Resizing_reports_when_there_was_nothing_to_resize()
    {
        // False also means "none found", which is what tells the caller to create one rather than assume the
        // correction is already recorded.
        var (account, fundId, _) = Seed(100m);

        Assert.False(account.Periods[0].ResizeReconciliationAdjustment(fundId, -20m));
    }

    [Fact]
    public void An_edit_does_not_strip_the_stamp()
    {
        // ⚠️ An edit mints a new expense id and copies the body across. Without the stamp in that list the entry
        // becomes exactly the orphan this whole mechanism exists to prevent.
        var (account, fundId, memberId) = Seed(100m);
        var period = account.Periods[0];
        account.RecordReconciliationAdjustment(period, fundId, -20m, period.To, memberId);
        var original = period.Expenses[0];

        var edited = period.EditExpense(original.Id, original.CategoryId, M(25m), fundId, "reworded", period.To);

        Assert.Equal(period.Id, edited.ReconciliationForPeriodId);
    }
}
