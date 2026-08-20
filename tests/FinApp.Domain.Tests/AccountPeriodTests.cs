using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

public class AccountPeriodTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Starting_period_can_copy_budgets_forward()
    {
        var account = new Account("Family", Eur);
        var food = account.AddCategory("Food");
        var bills = account.AddCategory("Bills");

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AddBudget(food.Id, M(400), alertThreshold: 0.75m, notifyOnEveryExpense: true);
        jan.AddBudget(bills.Id, M(250));

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), copyBudgetsFromPrevious: true);

        Assert.Equal(2, feb.Budgets.Count);
        var copiedFood = feb.FindBudget(food.Id);
        Assert.NotNull(copiedFood);
        Assert.Equal(M(400), copiedFood!.Allocated);
        Assert.Equal(0.75m, copiedFood.AlertThreshold);
        Assert.True(copiedFood.NotifyOnEveryExpense);
    }

    [Fact]
    public void Copying_budgets_forward_can_adjust_to_previous_consumption()
    {
        var account = new Account("Family", Eur);
        var food = account.AddCategory("Food");
        var bills = account.AddCategory("Bills");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AddBudget(food.Id, M(400));
        jan.AddBudget(bills.Id, M(250));
        jan.AddExpense(new Expense(food.Id, M(470), new DateOnly(2026, 1, 10), member, fund));  // overspent
        jan.AddExpense(new Expense(bills.Id, M(100), new DateOnly(2026, 1, 12), member, fund)); // underspent

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28),
            copyBudgetsFromPrevious: true, adjustToConsumption: true);

        // Overspent: halfway from 400 to 470 is 435, rounded up to the next 10 -> 440.
        Assert.Equal(M(440), feb.FindBudget(food.Id)!.Allocated);
        // Underspent: halfway from 250 to 100 is 175, rounded up to the next 10 -> 180.
        Assert.Equal(M(180), feb.FindBudget(bills.Id)!.Allocated);
    }

    [Fact]
    public void Settling_an_expense_reduces_it_and_unsettling_restores_it()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();
        var destAccount = Guid.NewGuid();

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(100), new DateOnly(2026, 1, 5), member, fund, onBehalfOfOtherAccount: true));

        var settlementId = Guid.NewGuid();
        var settled = period.SetSettlement(expense.Id, settlementId, destAccount, M(40));

        Assert.Equal(M(60), settled.Amount);                 // reduced by the settled amount
        Assert.Equal(40m, settled.SettledAmount);
        Assert.Equal(M(100), settled.OriginalAmount);
        Assert.True(settled.IsSettlementSource);
        Assert.Equal(destAccount, settled.SettledToAccountId);
        Assert.Equal(M(60), period.ExpensesTotal);           // only the un-settled portion is this account's cost

        // Re-settling recomputes from the original, not the already-reduced amount.
        var resettled = period.SetSettlement(settled.Id, settlementId, destAccount, M(70));
        Assert.Equal(M(30), resettled.Amount);

        // Unsettling (amount 0) restores the full amount and clears the link.
        var restored = period.SetSettlement(resettled.Id, settlementId, destAccount, M(0));
        Assert.Equal(M(100), restored.Amount);
        Assert.False(restored.IsSettlementSource);
        Assert.Null(restored.SettledToAccountId);
        Assert.Equal(M(100), period.ExpensesTotal);
    }

    /// <summary>
    /// ★ Owner ask: a refund had to be matched to a bank credit, so an expense paid from a wallet this app tracks
    /// itself could not be refunded at all. Shrinking such an expense already credits its own wallet — that is what
    /// <see cref="Period.FundBalance"/> does — so the ordinary case needs no movement and must not invent one.
    /// </summary>
    [Fact]
    public void A_refund_into_the_same_wallet_credits_it_without_inventing_a_transfer()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var cash = account.FundId("Cash");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(500), fundId: cash);
        var dinner = p.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), me.UserId, cash, "Dinner"));

        Assert.Equal(M(440), p.FundBalance(cash));

        account.RefundExpense(dinner.Id, M(20));   // no wallet named — the money went back where it came from

        Assert.Equal(M(460), p.FundBalance(cash));   // €20 back in the wallet, by the expense shrinking alone
        Assert.Empty(p.FundTransfers);               // and nothing invented to do it
        Assert.Equal(M(40), p.ExpensesTotal);
    }

    /// <summary>
    /// The case the wallet argument exists for: paid by card, handed back in cash. The expense's own wallet must not
    /// keep the money, and the wallet that received it must gain it.
    /// </summary>
    [Fact]
    public void A_refund_into_a_different_wallet_moves_the_money_there()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(500), fundId: bank);
        var dinner = p.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), me.UserId, bank, "Dinner"));

        account.RefundExpense(dinner.Id, M(20), cash);

        // The card is back where it was — the €20 it regained by the expense shrinking went straight out again.
        Assert.Equal(M(440), p.FundBalance(bank));
        Assert.Equal(M(20), p.FundBalance(cash));
        Assert.Equal(M(20), p.FundTransfers.Single().Amount);
        Assert.Equal(M(40), p.ExpensesTotal);   // and the spend still corrected itself
    }

    /// <summary>
    /// A synced wallet's real balance is the bank's, not the ledger's, so it is never debited by our own bookkeeping.
    /// The destination still gains. Same rule the bank money-in confirm follows.
    /// </summary>
    [Fact]
    public void A_refund_off_a_synced_wallet_does_not_debit_it()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var bank = account.FindFund(account.FundId("Bank"))!;
        bank.SetSynced(true);
        var cash = account.FundId("Cash");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(500), fundId: cash);
        var card = p.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), me.UserId, bank.Id, "Dinner"));
        card.SetFundSynced(true);

        var before = p.FundBalance(bank.Id);
        account.RefundExpense(card.Id, M(20), cash);

        Assert.Equal(before, p.FundBalance(bank.Id));   // untouched — the bank's own balance carries that side
        Assert.Equal(M(520), p.FundBalance(cash));
    }

    /// <summary>Restating a running total that has already been part-moved must not move the same euros twice.</summary>
    [Fact]
    public void A_second_refund_moves_only_what_is_newly_back()
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");
        var food = account.AddCategory("Food");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(500), fundId: bank);
        var dinner = p.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), me.UserId, bank, "Dinner"));

        var after1 = account.RefundExpense(dinner.Id, M(20), cash);
        account.RefundExpense(after1.Id, M(35), cash);   // the running TOTAL, not a second €35

        Assert.Equal(M(35), p.FundBalance(cash));
        Assert.Equal(2, p.FundTransfers.Count);
        Assert.Equal(M(15), p.FundTransfers.Last().Amount);   // only the newly-returned €15 moved
        Assert.Equal(M(25), p.ExpensesTotal);
    }

    [Fact]
    public void Refunding_an_expense_reduces_it_and_undoing_restores_it()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), member, fund, "Dinner"));

        // A friend pays back their share of the bill.
        var refunded = period.SetRefund(expense.Id, M(20));

        Assert.Equal(M(40), refunded.Amount);
        Assert.Equal(20m, refunded.RefundedAmount);
        Assert.Equal(M(60), refunded.AmountBeforeRefund);
        Assert.True(refunded.IsRefunded);
        // ★ The point of the feature: the period's spending falls. Booking the €20 as income would have left this
        // at €60 and added €20 of money-in nobody earned.
        Assert.Equal(M(40), period.ExpensesTotal);

        // A second friend pays up. The caller states the running total, so this does not have to know about the first.
        var again = period.SetRefund(refunded.Id, M(35));
        Assert.Equal(M(25), again.Amount);
        Assert.Equal(M(60), again.AmountBeforeRefund);

        // Undo restores the whole charge.
        var restored = period.SetRefund(again.Id, M(0));
        Assert.Equal(M(60), restored.Amount);
        Assert.False(restored.IsRefunded);
        Assert.Equal(M(60), period.ExpensesTotal);
    }

    [Fact]
    public void A_refund_cannot_exceed_the_expense_and_keeps_the_rows_links()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var trip = account.AddTrip("Lisbon", new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 9));
        var tag = account.AddTag("Split");
        var fund = Guid.NewGuid();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 1, 5), Guid.NewGuid(), fund, "Dinner"));
        expense.SetTags([tag.Id]);
        expense.SetTrip(trip.Id);
        expense.SetTime(new TimeOnly(20, 30));
        expense.SetFundSynced(true);
        expense.SetBankLink("bank-tx-1", autoFiled: true);

        Assert.Throws<InvalidOperationException>(() => period.SetRefund(expense.Id, M(61)));
        Assert.Throws<InvalidOperationException>(() => period.SetRefund(expense.Id, M(-1)));

        // ★ The rebuild mints a new id, so everything hanging off the row has to be carried over by hand. The bank
        // link is the one that matters most: lose it and the next sync offers to log this expense a second time.
        var refunded = period.SetRefund(expense.Id, M(20));
        Assert.Equal("bank-tx-1", refunded.BankExternalId);
        Assert.True(refunded.AutoFiled);
        Assert.True(refunded.FundSynced);
        Assert.Equal(trip.Id, refunded.TripId);
        Assert.Equal(new TimeOnly(20, 30), refunded.Time);
        Assert.Equal([tag.Id], refunded.TagIds);
        Assert.Equal("Dinner", refunded.Note);
    }

    [Fact]
    public void Duplicate_member_is_rejected()
    {
        var account = new Account("Shared", Eur);
        var userId = Guid.NewGuid();
        account.AddMember(userId, "Stoyan");

        Assert.Throws<InvalidOperationException>(() => account.AddMember(userId, "Stoyan again"));
    }

    [Fact]
    public void Deposits_accumulate_per_member()
    {
        var account = new Account("Shared", Eur);
        var a = account.AddMember(Guid.NewGuid(), "A");
        var b = account.AddMember(Guid.NewGuid(), "B");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(a.UserId, M(500));
        period.Deposit(a.UserId, M(100)); // a second deposit is its own row, not added onto the first
        period.Deposit(b.UserId, M(200));

        Assert.Equal(M(800), period.ContributionsPaidTotal);
        Assert.Equal(3, period.Contributions.Count);
        Assert.Equal(M(600), period.Contributions.Where(c => c.MemberId == a.UserId)
            .Aggregate(M(0), (acc, c) => acc + c.Paid));
    }

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively()
    {
        var account = new Account("Home", Eur);
        account.AddCategory("Food");
        Assert.Throws<InvalidOperationException>(() => account.AddCategory("food"));
        account.AddSavingCategory("Reserve");
        Assert.Throws<InvalidOperationException>(() => account.AddSavingCategory("RESERVE"));
        account.AddFund("Bank");
        Assert.Throws<InvalidOperationException>(() => account.AddFund(" bank "));

        var fun = account.AddCategory("Fun");
        Assert.Throws<InvalidOperationException>(() => account.RenameCategory(fun.Id, "Food")); // rename collision
    }

    [Fact]
    public void Expense_on_closed_period_is_rejected()
    {
        var account = new Account("Personal", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Close();

        Assert.Throws<InvalidOperationException>(() =>
            period.AddExpense(new Expense(food.Id, M(10), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid())));
    }
}
