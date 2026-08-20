using System.Collections;
using System.Reflection;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// ★ Every operation that changes an expense rebuilds it — the ledger is append-only, so editing, settling and
/// refunding each remove the row and add a new one with a new id. That means every field hanging off the row has to
/// be carried across by hand, and for a long time each of the three carried a *different* subset: the settle path
/// carried the installment link alone (dropping the label, the trip, the clock and the bank link), and the edit path
/// carried three of the eight (dropping the foreign figures and any refund).
///
/// <para>These tests are deliberately written by <b>reflection</b> rather than by listing the fields: a new property
/// on <see cref="Expense"/> joins the "must survive" set automatically and fails here until
/// <see cref="Expense.CopyBodyDataTo"/> carries it. Listing them by hand is exactly the thing that went wrong.</para>
/// </summary>
public class ExpenseBodyDataTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>Properties an operation is *allowed* to change; everything else must come out the other side equal.
    /// `Id` is always here — the rebuild mints a new one, which is the whole reason this problem exists.</summary>
    private static void AssertUnchanged(Expense before, Expense after, params string[] mayChange)
    {
        var allowed = new HashSet<string>(mayChange) { "Id" };
        foreach (var prop in typeof(Expense).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (allowed.Contains(prop.Name)) continue;
            var expected = Describe(prop.GetValue(before));
            var actual = Describe(prop.GetValue(after));
            Assert.True(expected == actual,
                $"{prop.Name} was not carried across the rebuild: expected '{expected}', got '{actual}'. " +
                "Add it to Expense.CopyBodyDataTo (or to this test's mayChange list if the operation really does change it).");
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        IEnumerable seq => string.Join(",", seq.Cast<object>().Select(x => x?.ToString() ?? "<null>")),
        _ => value.ToString() ?? "<null>",
    };

    /// <summary>An expense carrying one of everything, so nothing can be dropped without this noticing.</summary>
    private static (Account Account, Period Period, Expense Expense) FullyLoaded()
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
        expense.SetForeign(12.50m, "GBP");
        expense.SetInstallmentLink(Guid.NewGuid(), InstallmentPart.Principal, Guid.NewGuid());
        return (account, period, expense);
    }

    [Fact]
    public void Editing_an_expense_keeps_everything_it_was_not_asked_to_change()
    {
        var (_, period, expense) = FullyLoaded();
        var refunded = period.SetRefund(expense.Id, M(20));   // and a refund already recorded against it

        // Correct the note and nothing else. Everything the caller did not touch must come back untouched — the
        // foreign figure and the refund total are the two this path used to drop.
        var edited = period.EditExpense(refunded.Id, refunded.CategoryId, refunded.Amount, refunded.FundId, "Dinner, split", refunded.Date);

        AssertUnchanged(refunded, edited, "Note");
        Assert.Equal(12.50m, edited.ForeignAmount);
        Assert.Equal("GBP", edited.ForeignCurrency);
        Assert.Equal(20m, edited.RefundedAmount);
        // ★ And the refund stays undoable: without the total, "undo" would restore to the reduced figure.
        Assert.Equal(M(60), period.SetRefund(edited.Id, M(0)).Amount);
    }

    [Fact]
    public void Settling_an_expense_keeps_its_label_trip_time_and_bank_link()
    {
        var (_, period, expense) = FullyLoaded();
        var other = Guid.NewGuid();

        var settled = period.SetSettlement(expense.Id, Guid.NewGuid(), other, M(10));

        AssertUnchanged(expense, settled,
            "Amount", "AmountBeforeRefund", "SettlementId", "SettledToAccountId", "SettledAmount",
            "SettledMoney", "IsSettlementSource");
        // The bank link is the expensive one: lose it and the next sync offers to log this expense a second time.
        Assert.Equal("bank-tx-1", settled.BankExternalId);
        Assert.Equal(M(50), settled.Amount);
        Assert.Equal(M(60), settled.OriginalAmount);
    }

    [Fact]
    public void Refunding_an_expense_keeps_everything_the_other_two_paths_keep()
    {
        var (_, period, expense) = FullyLoaded();

        var refunded = period.SetRefund(expense.Id, M(20));

        AssertUnchanged(expense, refunded,
            "Amount", "RefundedAmount", "RefundedMoney", "IsRefunded", "OriginalAmount");
        Assert.Equal(M(60), refunded.AmountBeforeRefund);
    }
}
