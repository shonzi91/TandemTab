using System.Linq;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// A fund marked <see cref="Fund.IsSynced"/> mirrors a real bank balance, so entries created while it is
/// synced carry a per-entry marker that keeps them out of the fund's balance math (cases 4.1–4.4). Markers are
/// baked at creation, so toggling the flag never rewrites history and unsynced funds behave exactly as before.
/// </summary>
public class SyncedFundTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    private static Account Acc(out Period period)
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        return account;
    }

    private static Fund FundOf(Account a, string name) => a.Funds.First(f => f.Id == a.FundId(name));

    // 4.1 / 4.2 — an expense paid from a synced fund is logged but doesn't reduce the fund.
    [Fact]
    public void Expense_on_synced_fund_does_not_reduce_its_balance()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        FundOf(a, "Bank").SetSynced(true);

        var e = new Expense(Guid.NewGuid(), M(40), new DateOnly(2026, 1, 4), Guid.NewGuid(), bank);
        e.SetFundSynced(true);
        p.AddExpense(e);

        Assert.Equal(M(1000), p.FundBalance(bank));   // untouched — real bank balance is authoritative
        Assert.Equal(M(40), p.ExpensesTotal);          // but the spend is still recorded
    }

    [Fact]
    public void Expense_on_unsynced_fund_still_reduces_it()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));

        p.AddExpense(new Expense(Guid.NewGuid(), M(40), new DateOnly(2026, 1, 4), Guid.NewGuid(), bank));

        Assert.Equal(M(960), p.FundBalance(bank));
    }

    // 4.3 / 4.4 — a transfer moves only the unsynced side; the synced side is left to its real balance.
    [Fact]
    public void Transfer_from_synced_moves_only_the_unsynced_destination()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        var cash = a.FundId("Cash");
        p.SetInitialBalance(bank, M(1000));
        p.SetInitialBalance(cash, M(100));
        FundOf(a, "Bank").SetSynced(true);

        var t = p.TransferFunds(bank, cash, M(250), new DateOnly(2026, 1, 5));
        t.SetSyncedSides(fromSynced: true, toSynced: false);

        Assert.Equal(M(1000), p.FundBalance(bank));   // synced source unchanged
        Assert.Equal(M(350), p.FundBalance(cash));    // unsynced destination increased
    }

    [Fact]
    public void Transfer_to_synced_moves_only_the_unsynced_source()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        var cash = a.FundId("Cash");
        p.SetInitialBalance(bank, M(1000));
        p.SetInitialBalance(cash, M(100));
        FundOf(a, "Cash").SetSynced(true);

        var t = p.TransferFunds(bank, cash, M(250), new DateOnly(2026, 1, 5));
        t.SetSyncedSides(fromSynced: false, toSynced: true);

        Assert.Equal(M(750), p.FundBalance(bank));    // unsynced source decreased
        Assert.Equal(M(100), p.FundBalance(cash));    // synced destination unchanged
    }

    // Money-in modeled as a movement into the synced fund: destination synced (not credited), source moves.
    [Fact]
    public void Money_in_from_a_fund_moves_only_the_source()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");   // the synced Revolut fund
        var cash = a.FundId("Cash");   // the source the money came from
        p.SetInitialBalance(bank, M(1000));
        p.SetInitialBalance(cash, M(500));
        FundOf(a, "Bank").SetSynced(true);

        // €200 arrived in the synced fund from Cash.
        var t = p.TransferFunds(cash, bank, M(200), new DateOnly(2026, 1, 8));
        t.SetSyncedSides(fromSynced: false, toSynced: true);

        Assert.Equal(M(300), p.FundBalance(cash));    // source decreased
        Assert.Equal(M(1000), p.FundBalance(bank));   // synced destination unchanged (real balance handles it)
    }

    [Fact]
    public void Money_in_from_a_contributor_counts_as_income_without_crediting_the_fund()
    {
        var a = Acc(out var p);
        var owner = Guid.NewGuid();
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        FundOf(a, "Bank").SetSynced(true);

        var c = p.Deposit(owner, M(600), fundId: bank, date: new DateOnly(2026, 1, 2));
        c.SetFundSynced(true);

        Assert.Equal(M(600), p.ContributionsPaidTotal);   // still recorded as income/contributed
        Assert.Equal(M(1000), p.FundBalance(bank));        // but the synced fund isn't credited
    }

    [Fact]
    public void Deposit_into_synced_fund_does_not_increase_it()
    {
        var a = Acc(out var p);
        var owner = Guid.NewGuid();
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        FundOf(a, "Bank").SetSynced(true);

        var c = p.Deposit(owner, M(500), fundId: bank, date: new DateOnly(2026, 1, 3));
        c.SetFundSynced(true);

        Assert.Equal(M(1000), p.FundBalance(bank));
    }

    // Rollback / history: toggling the flag afterwards must not change already-recorded balances.
    [Fact]
    public void Toggling_sync_later_does_not_change_existing_entries()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        p.AddExpense(new Expense(Guid.NewGuid(), M(40), new DateOnly(2026, 1, 4), Guid.NewGuid(), bank));  // unsynced at creation
        Assert.Equal(M(960), p.FundBalance(bank));

        FundOf(a, "Bank").SetSynced(true);   // flip on now
        Assert.Equal(M(960), p.FundBalance(bank));   // the historical expense still counts
    }

    // #6 — a synced fund's carried-over closing is stored informative-only (kept out of InitialTotal), but it IS real
    // money that carried in, so it must count toward "money in" (and thus carry-over + the savings-rate denominator).
    [Fact]
    public void Synced_fund_informative_opening_counts_toward_money_in()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        var cash = a.FundId("Cash");
        FundOf(a, "Bank").SetSynced(true);
        p.SetInitialBalance(bank, M(144), informative: true);   // as StartNextPeriod stores a synced fund's opening
        p.Deposit(Guid.NewGuid(), M(1000), fundId: cash, date: new DateOnly(2026, 1, 2));   // fresh income

        var report = new FinApp.Domain.Services.SavingsReportService();
        Assert.Equal(M(0), p.InitialTotal);                 // informative opening is excluded from the ledger total
        Assert.Equal(M(1144), report.MoneyIn(a, p));        // 1000 fresh income + 144 carried in the synced fund
    }

    // --- Audit: cross-account & account-wide combos (no duplicate increase/decrease) ----------------------

    // Cross-account send from an UNSYNCED fund: the fund drops and so does the account's closing balance.
    [Fact]
    public void Send_to_another_account_from_unsynced_fund_reduces_the_fund_and_the_closing_balance()
    {
        var a = Acc(out var p);
        var cash = a.FundId("Cash");
        p.SetInitialBalance(cash, M(500));

        var outflow = p.TransferOut(cash, M(200), new DateOnly(2026, 1, 10), Guid.NewGuid());
        outflow.SetFundSynced(false);   // unsynced source

        Assert.Equal(M(300), p.FundBalance(cash));        // the fund really lost it
        Assert.Equal(M(200), p.ExternalOutTotal);
        Assert.Equal(M(300), p.ExpectedClosingBalance);   // 500 opening − 200 sent out
    }

    // Cross-account send from a SYNCED fund: the bank owns the fund position (unchanged here), but the money still
    // left the account — so the closing balance still drops. Counted once on the account side, no duplicate.
    [Fact]
    public void Send_to_another_account_from_synced_fund_leaves_the_fund_but_still_reduces_the_closing_balance()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        FundOf(a, "Bank").SetSynced(true);

        var outflow = p.TransferOut(bank, M(200), new DateOnly(2026, 1, 10), Guid.NewGuid());
        outflow.SetFundSynced(true);   // synced source — the real bank balance handles the fund position

        Assert.Equal(M(1000), p.FundBalance(bank));       // fund untouched (bank authoritative)
        Assert.Equal(M(800), p.ExpectedClosingBalance);   // but 200 really left the account
    }

    // The account-wide total DOES include synced flows (unlike per-fund balance): anchored on the synced fund's
    // opening balance and counting each flow once, the closing balance tracks the real bank while FundBalance stays
    // put (the bank is authoritative for the fund itself). This intended asymmetry is what avoids double-counting.
    [Fact]
    public void Synced_fund_closing_balance_tracks_the_bank_while_fund_balance_stays_put()
    {
        var a = Acc(out var p);
        var owner = Guid.NewGuid();
        var bank = a.FundId("Bank");
        p.SetInitialBalance(bank, M(1000));
        FundOf(a, "Bank").SetSynced(true);

        var income = p.Deposit(owner, M(600), fundId: bank, date: new DateOnly(2026, 1, 2));
        income.SetFundSynced(true);
        var spend = new Expense(Guid.NewGuid(), M(150), new DateOnly(2026, 1, 5), owner, bank);
        spend.SetFundSynced(true);
        p.AddExpense(spend);

        Assert.Equal(M(1000), p.FundBalance(bank));        // the fund itself: bank authoritative, unchanged
        Assert.Equal(M(1450), p.ExpectedClosingBalance);   // 1000 opening + 600 in − 150 out = the real bank trajectory
    }

    // The bank balance captured at rollover is stored as an INFORMATIVE opening: retrievable for showing a closed
    // period's "balance at close", but excluded from the real opening total so it never shifts the money model.
    [Fact]
    public void Synced_fund_informative_opening_is_stored_without_shifting_the_account_total()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        var cash = a.FundId("Cash");
        p.SetInitialBalance(cash, M(500));
        FundOf(a, "Bank").SetSynced(true);

        p.SetInitialBalance(bank, M(1000), informative: true);   // what the rollover captures for the synced fund

        Assert.Equal(M(1000), p.InitialBalances.First(b => b.FundId == bank).Amount);   // retrievable for display
        Assert.Equal(M(500), p.InitialTotal);                                          // excluded from the real opening total
        Assert.Equal(M(500), p.ExpectedClosingBalance);                                // account total unaffected — no double count
    }

    // Defensive: the domain handles a transfer with BOTH sides synced (e.g. a cross-account synced→synced move,
    // where each account marks its own side), moving neither tracked balance and leaving the account total intact.
    [Fact]
    public void Transfer_with_both_sides_synced_moves_neither_fund()
    {
        var a = Acc(out var p);
        var bank = a.FundId("Bank");
        var cash = a.FundId("Cash");
        p.SetInitialBalance(bank, M(1000));
        p.SetInitialBalance(cash, M(500));
        FundOf(a, "Bank").SetSynced(true);
        FundOf(a, "Cash").SetSynced(true);

        var t = p.TransferFunds(cash, bank, M(200), new DateOnly(2026, 1, 6));
        t.SetSyncedSides(fromSynced: true, toSynced: true);

        Assert.Equal(M(1000), p.FundBalance(bank));
        Assert.Equal(M(500), p.FundBalance(cash));
        Assert.Equal(M(1500), p.ExpectedClosingBalance);   // unchanged — an intra-account transfer is total-preserving
    }
}
