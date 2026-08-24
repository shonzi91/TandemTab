using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Money coming back on an expense from an <b>earlier</b> period (S119, owner report: *"I've paid 2 months ago
/// for a group and a member gave me their part this one"*).
///
/// <para>★★ <b>The hard part is not finding the expense, it is saying where the money is now.</b> Shrinking a June
/// expense credits June's closing balance — and opening balances are snapshotted when a period rolls, so nothing
/// carries that credit into August. Left there the refund would be recorded truthfully and be invisible: the app
/// would show less cash than the wallet holds, silently. These tests exist mostly to pin that half.</para>
/// </summary>
public class CrossPeriodRefundTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>June with a €60 group dinner paid from Cash, then July and August opened on top — the opening
    /// balances snapshotted as the app does at roll time. Returns the account, the dinner, and August.</summary>
    private static (Account Account, Expense Dinner, Period August, Guid Cash, Guid Bank) Seed(decimal opening = 500m)
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var cash = account.FundId("Cash");
        var bank = account.FundId("Bank");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var june = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        june.SetInitialBalance(cash, M(opening));
        var dinner = june.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 6, 12), me.UserId, cash, "Group dinner"));

        // Roll twice, carrying the real closing balance forward each time — the snapshot behaviour that makes a
        // retroactive edit to June invisible to August.
        var july = account.StartPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        july.SetInitialBalance(cash, june.ExpectedClosingBalance);
        var august = account.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        august.SetInitialBalance(cash, july.ExpectedClosingBalance);

        return (account, dinner, august, cash, bank);
    }

    [Fact]
    public void A_refund_can_reach_an_expense_from_an_earlier_period()
    {
        var (account, dinner, _, _, _) = Seed();

        var refunded = account.RefundExpense(dinner.Id, M(20));

        // The charge is corrected where the charge lives — June's dinner turned out to cost €40, and June's Food
        // budget corrects itself with it.
        var june = account.Periods[0];
        Assert.Equal(M(40), june.ExpensesTotal);
        Assert.Equal(20m, refunded.RefundedAmount);
        Assert.Equal(M(60), refunded.AmountBeforeRefund);
    }

    [Fact]
    public void The_money_lands_in_the_period_it_actually_arrived_in()
    {
        var (account, dinner, august, cash, _) = Seed();
        var before = august.FundBalance(cash);

        account.RefundExpense(dinner.Id, M(20));

        // ★★ The half that makes this feature honest rather than merely permitted. Without it the €20 sits in
        // June's closing balance, which nothing reads any more, and the wallet the user is actually holding shows
        // €20 less than it contains — with nothing on screen to explain the gap.
        Assert.Equal(before + M(20), august.FundBalance(cash));
    }

    [Fact]
    public void Undoing_a_cross_period_refund_takes_the_same_money_back_out()
    {
        var (account, dinner, august, cash, _) = Seed();
        var before = august.FundBalance(cash);

        var refunded = account.RefundExpense(dinner.Id, M(20));
        account.RefundExpense(refunded.Id, M(0), refunded.FundId);

        // ⚠️ Symmetry is the whole point: an undo that only put the expense back would leave the account
        // permanently €20 richer, which is a worse bug than the one this feature fixes.
        Assert.Equal(before, august.FundBalance(cash));
        Assert.Equal(M(60), account.Periods[0].ExpensesTotal);
    }

    [Fact]
    public void The_undo_reverses_the_wallet_the_money_went_INTO_not_the_one_it_was_paid_from()
    {
        var (account, dinner, august, cash, bank) = Seed();
        var cashBefore = august.FundBalance(cash);
        var bankBefore = august.FundBalance(bank);

        // Paid from Cash in June, handed back into Bank in August.
        var refunded = account.RefundExpense(dinner.Id, M(20), bank);
        Assert.Equal(bankBefore + M(20), august.FundBalance(bank));
        Assert.Equal(cashBefore, august.FundBalance(cash));

        // The undo route knows only the expense, so without the row remembering where the money went it would
        // guess Cash — crediting one wallet and debiting another, and leaving both wrong.
        account.RefundExpense(refunded.Id, M(0), refunded.FundId);

        Assert.Equal(bankBefore, august.FundBalance(bank));
        Assert.Equal(cashBefore, august.FundBalance(cash));
    }

    [Fact]
    public void A_synced_wallet_is_left_alone_because_the_bank_already_counted_it()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        account.SetFundSynced(bank, true);
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var june = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var dinner = june.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 6, 12), me.UserId, bank, "Group dinner"));
        var august = account.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        var openingBefore = august.OpeningBalanceOf(bank);

        account.RefundExpense(dinner.Id, M(20), bank);

        // ⚠️ A synced wallet's balance IS the bank's, and the bank counted the credit the moment it landed.
        // Adjusting the opening balance too would show the €20 twice — which is the mirror of the bug the
        // adjustment exists to prevent, and just as silent.
        Assert.Equal(openingBefore, august.OpeningBalanceOf(bank));
        Assert.Equal(M(40), june.ExpensesTotal);   // the charge is still corrected
    }

    [Fact]
    public void A_closed_period_is_no_obstacle_to_recording_what_a_purchase_cost()
    {
        var (account, dinner, _, _, _) = Seed();
        var june = account.Periods[0];
        june.Close();

        account.RefundExpense(dinner.Id, M(20));

        // Recording a refund is not a settled month's spending being revised — it is that purchase's record being
        // completed. See the allowClosed note on Period.SetRefund.
        Assert.Equal(M(40), june.ExpensesTotal);
    }

    [Fact]
    public void A_same_period_refund_still_behaves_exactly_as_it_did()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var cash = account.FundId("Cash");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(500), fundId: cash);
        var dinner = p.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), me.UserId, cash, "Dinner"));

        account.RefundExpense(dinner.Id, M(20));

        // No opening-balance adjustment, no transfer — the shrink alone credits the wallet, and inventing either
        // would count the same €20 twice.
        Assert.Equal(M(460), p.FundBalance(cash));
        Assert.Empty(p.FundTransfers);
        Assert.Equal(M(0), p.OpeningBalanceOf(cash));
    }

    [Fact]
    public void The_refund_survives_the_rebuild_that_records_it()
    {
        var (account, dinner, _, _, bank) = Seed();

        var refunded = account.RefundExpense(dinner.Id, M(20), bank);
        var reloaded = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        var row = reloaded.Periods[0].Expenses.Single();

        // Body data, so no schema change — and it has to survive a round trip or the undo forgets where the money
        // went the first time the app is reloaded.
        Assert.Equal(20m, row.RefundedAmount);
        Assert.Equal(bank, row.RefundedToFundId);
    }
}
