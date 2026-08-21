using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// D1: an expense in one account counted toward a trip in another. The money never moves — it stays in the period,
/// spending and budgets of the account that paid — and only the trip's recap reaches across to gather it, saying
/// where it came from when it does.
/// </summary>
public class CrossAccountTripTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jun7 = new(2026, 6, 7);
    private static readonly DateOnly Jun30 = new(2026, 6, 30);

    /// <summary>Account B — the one that owns the trip.</summary>
    private static Account Host(out Trip trip, out Guid fund, out Guid cat)
    {
        var account = new Account("Joint", Eur);
        account.AddDefaultFunds();
        cat = account.AddCategory("Travel").Id;
        fund = account.FundId("Bank");
        trip = account.AddTrip("Rome", Jun1, Jun7);
        account.StartPeriod(Jun1, Jun30).SetInitialBalance(fund, M(2_000m));
        return account;
    }

    /// <summary>Account A — the one whose card paid, with one expense linked to B's trip.</summary>
    private static Account Payer(Guid tripId, Guid hostAccountId, decimal amount, out Expense linked, string currency = Eur)
    {
        var account = new Account("Mine", currency);
        account.AddDefaultFunds();
        var cat = account.AddCategory("Flights").Id;
        var fund = account.FundId("Bank");
        var period = account.StartPeriod(Jun1, Jun30);
        period.SetInitialBalance(fund, new Money(3_000m, currency));
        linked = period.AddExpense(new Expense(cat, new Money(amount, currency), new DateOnly(2026, 5, 20), Guid.NewGuid(), fund, "Flights"));
        linked.SetTrip(tripId, hostAccountId);
        return account;
    }

    private static ForeignTripExpense Row(Account payer, Expense e, string category = "Flights", string? tag = null) =>
        new(payer.Id, payer.Name, e, category, tag);

    [Fact]
    public void The_total_includes_the_other_accounts_rows_and_names_what_they_came_to()
    {
        var host = Host(out var trip, out var fund, out var cat);
        host.CurrentPeriod!.AddExpense(new Expense(cat, M(200m), Jun(3), Guid.NewGuid(), fund, "Hotel")).SetTrip(trip.Id);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(M(680m), recap.Spent);
        Assert.Equal(M(480m), recap.PaidFromOtherAccounts);
        Assert.True(recap.HasOtherAccountSpend);
        Assert.Equal(2, recap.ExpenseCount);
    }

    private static DateOnly Jun(int day) => new(2026, 6, day);

    [Fact]
    public void Every_contributing_account_is_named_including_this_one()
    {
        // A breakdown listing only the OTHERS would leave the reader working out their own share by subtraction.
        var host = Host(out var trip, out var fund, out var cat);
        host.CurrentPeriod!.AddExpense(new Expense(cat, M(200m), Jun(3), Guid.NewGuid(), fund, "Hotel")).SetTrip(trip.Id);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(2, recap.SourceAccountBreakdown.Count);
        Assert.Equal(M(480m), recap.SourceAccountBreakdown.Single(s => s.Id == payer.Id).Total);
        Assert.Equal(M(200m), recap.SourceAccountBreakdown.Single(s => s.Id == host.Id).Total);
        Assert.Equal("Mine", recap.ForeignName(payer.Id));
    }

    [Fact]
    public void A_foreign_slice_carries_its_own_resolved_name_never_a_placeholder()
    {
        // ⚠️ The thing that looks broken in the running app: the category id belongs to the account that PAID, so
        // looking it up in the host finds nothing and the wedge renders literally as "—".
        var host = Host(out var trip, out _, out _);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        var slice = Assert.Single(recap.CategoryBreakdown);
        Assert.Null(host.FindCategory(slice.Id));            // the host genuinely cannot resolve it
        Assert.Equal("Flights", recap.ForeignName(slice.Id));  // ...and the recap can
    }

    [Fact]
    public void The_biggest_line_names_the_account_that_paid_it()
    {
        var host = Host(out var trip, out var fund, out var cat);
        host.CurrentPeriod!.AddExpense(new Expense(cat, M(200m), Jun(3), Guid.NewGuid(), fund, "Hotel")).SetTrip(trip.Id);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(M(480m), recap.Biggest!.Amount);
        Assert.Equal(payer.Id, recap.Biggest.PaidFromAccountId);
    }

    [Fact]
    public void Savings_funding_never_counts_a_foreign_row()
    {
        // Today the ids simply never collide, but a money figure resting on two Guids not matching is not a rule
        // anyone can check later — so the guard is explicit and this pins it.
        var host = Host(out var trip, out _, out _);
        var bucket = host.AddSavingCategory("Rome fund");
        host.SetTripSavingCategory(trip.Id, bucket.Id);

        // A foreign row built to LOOK savings-funded against the HOST's own bucket id — the collision the guard
        // exists for, forced rather than waited for.
        var payer = new Account("Mine", Eur);
        payer.AddDefaultFunds();
        var payerCat = payer.AddCategory("Flights").Id;
        var payerFund = payer.FundId("Bank");
        var payerPeriod = payer.StartPeriod(Jun1, Jun30);
        payerPeriod.SetInitialBalance(payerFund, M(3_000m));
        var flight = payerPeriod.AddExpense(new Expense(payerCat, M(480m), new DateOnly(2026, 5, 20), Guid.NewGuid(),
            payerFund, "Flights", sourceSavingCategoryId: bucket.Id));
        flight.SetTrip(trip.Id, host.Id);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(M(0m), recap.FundedFromSavings);
        Assert.Equal(M(480m), recap.Spent);   // still counted as spending — it was
    }

    [Fact]
    public void A_row_in_another_currency_is_left_out_rather_than_taking_the_page_down()
    {
        // Money's + throws on a mismatch and this sum feeds the whole Trips screen server-side. The attach gate is
        // the real fix; this is the blast radius if one ever slips past it.
        var host = Host(out var trip, out _, out _);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight, currency: "BGN");

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(M(0m), recap.Spent);
        Assert.Equal(M(0m), recap.PaidFromOtherAccounts);
    }

    [Fact]
    public void With_no_foreign_rows_the_recap_is_what_it_always_was()
    {
        // The regression pin for the 45 existing TripTests: a trailing optional must change nothing when absent.
        var host = Host(out var trip, out var fund, out var cat);
        host.CurrentPeriod!.AddExpense(new Expense(cat, M(200m), Jun(3), Guid.NewGuid(), fund, "Hotel")).SetTrip(trip.Id);

        var recap = new TripRecapService().Build(host, trip.Id)!;

        Assert.Equal(M(200m), recap.Spent);
        Assert.Equal(M(0m), recap.PaidFromOtherAccounts);
        Assert.False(recap.HasOtherAccountSpend);
        Assert.Empty(recap.SourceAccountBreakdown);
        Assert.Null(recap.Biggest!.PaidFromAccountId);
    }

    [Fact]
    public void PerDay_still_divides_by_the_trips_own_length()
    {
        // 7 days, whoever paid — the denominator is a fact about the journey, not about the ledger.
        var host = Host(out var trip, out _, out _);
        var payer = Payer(trip.Id, host.Id, 700m, out var flight);

        var recap = new TripRecapService().Build(host, trip.Id, [Row(payer, flight)])!;

        Assert.Equal(7, recap.LengthInDays);
        Assert.Equal(M(100m), recap.PerDay);
    }

    [Fact]
    public void A_foreign_linked_expense_never_shows_in_its_OWN_accounts_trip_lists()
    {
        // The link points at the OTHER account's trip. Without the TripAccountId guard, an id collision would
        // surface it here — vanishingly unlikely, but the guard makes the safety local rather than a coincidence.
        var host = Host(out var trip, out _, out _);
        var payer = Payer(trip.Id, host.Id, 480m, out _);

        Assert.Empty(payer.TripExpenses(trip.Id));
        Assert.Single(payer.ExpensesOnForeignTrip(trip.Id, host.Id));
    }

    [Fact]
    public void Deleting_the_trip_cannot_reach_the_other_accounts_expense_and_does_not_try()
    {
        // The dangling pointer is deliberate and harmless — ids are freshly minted and never reused, so a new trip
        // can't adopt the orphan. Cascading instead would need an unbounded write across accounts B can't list.
        var host = Host(out var trip, out _, out _);
        var payer = Payer(trip.Id, host.Id, 480m, out var flight);

        host.RemoveTrip(trip.Id);

        Assert.Equal(trip.Id, flight.TripId);        // still points at a trip that is gone
        Assert.Equal(host.Id, flight.TripAccountId);
        Assert.Null(host.FindTrip(trip.Id));
    }

    [Fact]
    public void The_source_account_directory_is_idempotent()
    {
        // MutateTwoAsync may replay its mutation after losing a concurrency race, so the attach must be re-runnable.
        var host = Host(out var trip, out _, out _);
        var payerId = Guid.NewGuid();

        trip.AddSourceAccount(payerId);
        trip.AddSourceAccount(payerId);
        Assert.Single(trip.SourceAccountIds);

        trip.RemoveSourceAccount(payerId);
        trip.RemoveSourceAccount(payerId);
        Assert.Empty(trip.SourceAccountIds);
    }

    [Fact]
    public void Clearing_the_trip_clears_the_account_that_owned_it()
    {
        // An account id left on an unattached expense is a fact about nothing, and the next reader would have to
        // guess whether it once meant something.
        var host = Host(out var trip, out _, out _);
        Payer(trip.Id, host.Id, 480m, out var flight);

        flight.SetTrip(null);

        Assert.Null(flight.TripId);
        Assert.Null(flight.TripAccountId);
    }
}
