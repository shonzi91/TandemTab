using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// D1: an expense in one account counted toward a trip in another. The invariant running through all of these is
/// that the <b>money never moves</b> — the expense stays in the paying account's period, spending and budgets —
/// while the trip's total reaches across and says where each part came from.
/// </summary>
public class CrossAccountTripApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public CrossAccountTripApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly PeriodFrom = new(2026, 6, 1);
    private static readonly DateOnly PeriodTo = new(2026, 6, 30);
    private static readonly DateOnly TripFrom = new(2026, 6, 10);
    private static readonly DateOnly TripTo = new(2026, 6, 17);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name, string currency = "EUR") =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, currency)))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Category, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId,
        string categoryName = "Food", string currency = "EUR")
    {
        var agg = new Account("Seed", currency);
        agg.AddDefaultFunds();
        var category = agg.AddCategory(categoryName).Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(PeriodFrom, PeriodTo).Deposit(memberId, new Money(3000m, currency), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    private static Task<Guid> CreateTrip(HttpClient client, Guid accountId) =>
        IdOf(client.PostAsJsonAsync($"/accounts/{accountId}/trips",
            new CreateTripRequest("Rome", TripFrom, TripTo, "Rome, Italy", "🇮🇹")).Result);

    private static async Task<Account> Snapshot(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    /// <summary>Two accounts of the same user: "Joint" owns a trip, "Mine" holds an expense. Returns both, plus the
    /// trip and the expense.</summary>
    private async Task<(HttpClient Client, Guid Payer, Guid Host, Guid TripId, Guid ExpenseId)> TwoAccountsAsync(string seed)
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync(seed);
        var payer = await CreateAccount(client, "Mine");
        var host = await CreateAccount(client, "Joint");
        var (payerCat, payerFund) = await SeedAsync(client, payer.Id, auth.UserId, "Flights");
        await SeedAsync(client, host.Id, auth.UserId, "Travel");
        var tripId = await CreateTrip(client, host.Id);
        var expenseId = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer.Id}/expenses",
            new AddExpenseRequest(payerCat, 480m, payerFund, new DateOnly(2026, 6, 2), "Flight")));
        return (client, payer.Id, host.Id, tripId, expenseId);
    }

    [Fact]
    public async Task Attaching_to_another_accounts_trip_writes_both_sides()
    {
        var (client, payer, host, tripId, expenseId) = await TwoAccountsAsync("xtrip_attach");

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        // The paying account carries the qualified link...
        var payerExpense = (await Snapshot(client, payer)).CurrentPeriod!.Expenses.Single(e => e.Id == expenseId);
        Assert.Equal(tripId, payerExpense.TripId);
        Assert.Equal(host, payerExpense.TripAccountId);
        // ...and the trip's directory now knows where to look.
        Assert.Equal([payer], (await Snapshot(client, host)).Trips.Single().SourceAccountIds);
    }

    [Fact]
    public async Task The_money_stays_in_the_account_that_paid()
    {
        // ★ The whole safety argument, asserted as numbers: the host's own totals must not move at all.
        var (client, payer, host, tripId, expenseId) = await TwoAccountsAsync("xtrip_money");
        var hostBefore = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{host}/overview");

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        var hostAfter = await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{host}/overview");
        Assert.Equal(hostBefore!.Spent, hostAfter!.Spent);
        Assert.Equal(hostBefore.Current, hostAfter.Current);
        // And it is still the payer's spending, because the payer paid.
        Assert.Equal(480m, (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{payer}/overview"))!.Spent);
    }

    [Fact]
    public async Task The_hosts_trip_view_reports_the_foreign_row_with_Spent_left_alone()
    {
        // ⚠️ `Spent` stays own-account-only so an older client shows a total its own ledger adds up to; the
        // combined figure is a separate trailing field. See TripDto.
        var (client, payer, host, tripId, expenseId) = await TwoAccountsAsync("xtrip_view");

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        var trip = (await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{host}/trips"))!.Trips.Single();
        Assert.Equal(0m, trip.Spent);
        Assert.Equal(480m, trip.SpentIncludingOtherAccounts);
        Assert.Equal(480m, trip.PaidFromOtherAccounts);
        Assert.Equal("Mine", trip.BySourceAccount!.Single(s => s.Id == payer).Label);
    }

    [Fact]
    public async Task The_detail_names_the_foreign_rows_category_and_the_account_that_paid()
    {
        // The thing that would look broken in the running app: a category minted in the paying account resolves to
        // nothing in the host, so both the slice and the row would render as "—".
        var (client, payer, host, tripId, expenseId) = await TwoAccountsAsync("xtrip_detail");

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        var detail = (await client.GetFromJsonAsync<TripDetailDto>($"/accounts/{host}/trips/{tripId}"))!;
        Assert.Equal("Flights", detail.Slices.Single().Label);
        var row = Assert.Single(detail.Expenses);
        Assert.Equal("Flights", row.CategoryName);
        Assert.Equal(payer, row.PaidFromAccountId);
        Assert.Equal("Mine", row.PaidFromAccountName);
        Assert.Equal(payer, detail.Biggest!.PaidFromAccountId);
    }

    [Fact]
    public async Task A_trip_in_an_account_the_caller_cannot_read_is_not_found()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xtrip_auth_a");
        var payer = await CreateAccount(client, "Mine");
        var (cat, fund) = await SeedAsync(client, payer.Id, auth.UserId, "Flights");
        var expenseId = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer.Id}/expenses",
            new AddExpenseRequest(cat, 480m, fund, new DateOnly(2026, 6, 2), "Flight")));

        // A different user's account, with a trip of its own.
        var (other, otherAuth) = await _factory.RegisterAndAuthAsync("xtrip_auth_b");
        var stranger = await CreateAccount(other, "Theirs");
        await SeedAsync(other, stranger.Id, otherAuth.UserId, "Travel");
        var strangerTrip = await CreateTrip(other, stranger.Id);

        var response = await client.PutAsJsonAsync($"/accounts/{payer.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(strangerTrip, stranger.Id));

        // 404, not 403 — the repo's convention: never leak whether an account exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_currency_mismatch_is_refused_at_the_attach()
    {
        // ⚠️ A hard gate, not a display choice: Money's + throws on a mismatch and that sum feeds the host's whole
        // Trips screen server-side, so one stray row would take the page down rather than render oddly.
        var (client, auth) = await _factory.RegisterAndAuthAsync("xtrip_ccy");
        var payer = await CreateAccount(client, "Mine");
        var host = await CreateAccount(client, "Joint", "BGN");
        var (cat, fund) = await SeedAsync(client, payer.Id, auth.UserId, "Flights");
        await SeedAsync(client, host.Id, auth.UserId, "Travel", "BGN");
        var tripId = await CreateTrip(client, host.Id);
        var expenseId = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer.Id}/expenses",
            new AddExpenseRequest(cat, 480m, fund, new DateOnly(2026, 6, 2), "Flight")));

        var response = await client.PutAsJsonAsync($"/accounts/{payer.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_trip_id_that_isnt_in_the_named_account_is_rejected()
    {
        var (client, payer, host, _, expenseId) = await TwoAccountsAsync("xtrip_badtrip");

        var response = await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(Guid.NewGuid(), host));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detaching_the_last_row_removes_this_account_from_the_trips_directory()
    {
        var (client, payer, host, tripId, expenseId) = await TwoAccountsAsync("xtrip_detach");
        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(null))).EnsureSuccessStatusCode();

        Assert.Null((await Snapshot(client, payer)).CurrentPeriod!.Expenses.Single(e => e.Id == expenseId).TripId);
        Assert.Empty((await Snapshot(client, host)).Trips.Single().SourceAccountIds);
    }

    [Fact]
    public async Task Detaching_one_of_two_rows_leaves_the_directory_alone()
    {
        // ⚠️ Removing the account on the first detach would hide the OTHER attachment from the recap — the total
        // would drop by a row nobody touched.
        var (client, payer, host, tripId, firstExpense) = await TwoAccountsAsync("xtrip_detach_one");
        var payerAgg = await Snapshot(client, payer);
        var cat = payerAgg.Categories.First(c => c.Name == "Flights").Id;
        var fund = payerAgg.FundId("Bank");
        var secondExpense = await IdOf(await client.PostAsJsonAsync($"/accounts/{payer}/expenses",
            new AddExpenseRequest(cat, 120m, fund, new DateOnly(2026, 6, 3), "Transfer")));

        foreach (var id in new[] { firstExpense, secondExpense })
            (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{id}/trip",
                new SetExpenseTripRequest(tripId, host))).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync($"/accounts/{payer}/expenses/{firstExpense}/trip",
            new SetExpenseTripRequest(null))).EnsureSuccessStatusCode();

        Assert.Equal([payer], (await Snapshot(client, host)).Trips.Single().SourceAccountIds);
        Assert.Equal(120m, (await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{host}/trips"))!
            .Trips.Single().PaidFromOtherAccounts);
    }

    [Fact]
    public async Task An_ordinary_same_account_attach_is_unchanged()
    {
        // The regression pin: the trailing-optional field must leave the single-account path exactly as it was.
        var (client, auth) = await _factory.RegisterAndAuthAsync("xtrip_plain");
        var account = await CreateAccount(client, "Mine");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId, "Flights");
        var tripId = await CreateTrip(client, account.Id);
        var expenseId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 480m, fund, new DateOnly(2026, 6, 2), "Flight")));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId))).EnsureSuccessStatusCode();

        var expense = (await Snapshot(client, account.Id)).CurrentPeriod!.Expenses.Single(e => e.Id == expenseId);
        Assert.Equal(tripId, expense.TripId);
        Assert.Null(expense.TripAccountId);

        var trip = (await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{account.Id}/trips"))!.Trips.Single();
        Assert.Equal(480m, trip.Spent);
        Assert.Equal(0m, trip.PaidFromOtherAccounts);
        Assert.Null(trip.BySourceAccount);
    }
}
