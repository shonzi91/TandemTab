using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The trip <b>read</b> model — the half that didn't exist until R2 went looking for it. Trips were write-only over
/// the API (five commands, no way to see what they'd done), because the thick client reads them out of the snapshot
/// it already carries. These pin the two things a thin client can't re-derive for itself: what a journey has cost,
/// gathered by link across every period, and which of the four states it is in on the caller's own date.
/// </summary>
public class TripsViewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public TripsViewApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly From = new(2026, 6, 10);
    private static readonly DateOnly To = new(2026, 6, 17);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Category, Guid Fund, Guid Bucket)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        var bucket = agg.AddSavingCategory("Holiday").Id;
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        period.Deposit(memberId, new Money(3000m, "EUR"), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund, bucket);
    }

    private static async Task<Guid> CreateTrip(HttpClient client, Guid accountId, string name, DateOnly from, DateOnly to)
    {
        var resp = await client.PostAsJsonAsync($"/accounts/{accountId}/trips",
            new CreateTripRequest(name, from, to, $"{name}, somewhere", "🇮🇹"));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    private static Task<TripsViewDto?> Trips(HttpClient client, Guid accountId, DateOnly today) =>
        client.GetFromJsonAsync<TripsViewDto>($"/accounts/{accountId}/trips?today={today:yyyy-MM-dd}");

    [Fact]
    public async Task An_account_with_no_snapshot_reads_as_an_empty_list_not_a_404()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tripsview_empty");
        var account = await CreateAccount(client, "Trips");

        var view = (await Trips(client, account.Id, new DateOnly(2026, 6, 12)))!;

        Assert.Empty(view.Trips);
        Assert.Empty(view.TripTags);
    }

    [Fact]
    public async Task A_trips_total_is_gathered_by_link_across_periods_and_split_around_the_dates()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_total");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", From, To);

        // A booking paid before leaving, a dinner while away, a late charge after getting home. All three belong to
        // the trip because they carry its id — the June period they happen to sit in is not what makes them a trip.
        foreach (var (amount, date, note) in new[]
                 {
                     (220m, new DateOnly(2026, 6, 2), "Flight"),
                     (60m, new DateOnly(2026, 6, 12), "Dinner"),
                     (18m, new DateOnly(2026, 6, 20), "Late card charge"),
                 })
        {
            (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
                new AddExpenseRequest(cat, amount, fund, date, note, TripId: tripId))).EnsureSuccessStatusCode();
        }

        var trip = Assert.Single((await Trips(client, account.Id, new DateOnly(2026, 6, 21)))!.Trips);

        Assert.Equal(298m, trip.Spent);
        Assert.Equal(3, trip.ExpenseCount);
        Assert.Equal(220m, trip.PrePaid);
        Assert.Equal(60m, trip.OnTrip);
        Assert.Equal(18m, trip.AfterReturn);
        Assert.Equal(8, trip.LengthInDays);          // both ends inclusive
    }

    // Starting and finishing are written against the SERVER's date on purpose ("we've left" is a fact about now,
    // and a device with a wrong clock shouldn't be able to write one) — and the domain refuses to start a trip that
    // isn't running today. So any test that confirms a departure has to be built around the real today, not a fixed
    // calendar date. The `today` the view is asked about is still the caller's, which is what these check.
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly RunFrom = Today.AddDays(-2);
    private static readonly DateOnly RunTo = Today.AddDays(5);

    [Fact]
    public async Task A_dated_trip_is_only_active_once_the_departure_is_confirmed()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_state");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", RunFrom, RunTo);

        // Before the dates: upcoming, counting down.
        var before = Assert.Single((await Trips(client, account.Id, RunFrom.AddDays(-3)))!.Trips);
        Assert.Equal("upcoming", before.State);
        Assert.Equal(3, before.DaysUntil);
        Assert.Null(before.Day);

        // ★ Inside the dates but unconfirmed: awaiting-start, NOT active. This is the whole reason the state is
        // computed rather than inferred from `today >= From` — a date is not a departure.
        var due = Assert.Single((await Trips(client, account.Id, RunFrom))!.Trips);
        Assert.Equal("awaiting-start", due.State);
        Assert.Null(due.Day);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}/started", new StartTripRequest(true)))
            .EnsureSuccessStatusCode();

        var running = Assert.Single((await Trips(client, account.Id, RunFrom.AddDays(2)))!.Trips);
        Assert.Equal("active", running.State);
        Assert.Equal(3, running.Day);                // the From day is day 1
        Assert.Null(running.DaysUntil);

        // Past the last day, no Finish needed.
        var over = Assert.Single((await Trips(client, account.Id, RunTo.AddDays(1)))!.Trips);
        Assert.Equal("finished", over.State);
    }

    [Fact]
    public async Task Finishing_beats_the_calendar()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_finish");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", RunFrom, RunTo);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}/started", new StartTripRequest(true)))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}/finished", new FinishTripRequest(true)))
            .EnsureSuccessStatusCode();

        // Today still falls inside the trip's dates, and it is still over: the traveller said so.
        var trip = Assert.Single((await Trips(client, account.Id, RunFrom.AddDays(1)))!.Trips);
        Assert.Equal("finished", trip.State);
        Assert.NotNull(trip.FinishedOn);
    }

    [Fact]
    public async Task Trips_read_newest_departure_first_and_carry_their_budget_and_savings_link()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_order");
        var account = await CreateAccount(client, "Trips");
        var (_, _, bucket) = await SeedAsync(client, account.Id, auth.UserId);
        await CreateTrip(client, account.Id, "Spring", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 5));
        var later = await CreateTrip(client, account.Id, "Rome", From, To);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{later}",
            new EditTripRequest("Rome", From, To, "Rome, Italy", "🇮🇹", bucket, 900m))).EnsureSuccessStatusCode();

        var view = (await Trips(client, account.Id, new DateOnly(2026, 6, 12)))!;

        Assert.Equal(new[] { "Rome", "Spring" }, view.Trips.Select(t => t.Name));
        var rome = view.Trips[0];
        Assert.Equal(900m, rome.Budget);
        Assert.Equal(bucket, rome.SavingCategoryId);
        Assert.Equal("Holiday", rome.SavingCategoryName);   // resolved server-side; the client has no bucket list here
    }

    [Fact]
    public async Task Seeded_trip_labels_are_listed_with_the_category_they_file_into()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_tags");
        var account = await CreateAccount(client, "Trips");
        var (cat, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/trip-tags",
            new SeedTripTagsRequest([new TripTagSeed("Stay", "house", cat), new TripTagSeed("Travel", "plane")])))
            .EnsureSuccessStatusCode();

        var view = (await Trips(client, account.Id, new DateOnly(2026, 6, 12)))!;

        Assert.Equal(2, view.TripTags.Count);
        var stay = view.TripTags.Single(t => t.Name == "Stay");
        Assert.Equal("house", stay.Icon);
        Assert.Equal(cat, stay.CategoryId);
    }

    [Fact]
    /// <summary>
    /// The ledger runs newest first — a trip is read as the days you lived, and a size order turns it into a
    /// leaderboard. ★ <see cref="TripDetailDto.Biggest"/> is computed on its own rather than taken as the head of
    /// the list, so re-ordering the ledger can't silently make "biggest single thing" mean "most recent".
    /// </summary>
    public async Task A_trips_detail_lists_its_expenses_newest_first_and_says_which_side_of_the_journey_each_falls_on()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripdetail_rows");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", From, To);
        foreach (var (amount, date, note) in new[]
                 {
                     (220m, new DateOnly(2026, 6, 2), "Flight"),
                     (60m, new DateOnly(2026, 6, 12), "Dinner"),
                     (18m, new DateOnly(2026, 6, 20), "Late charge"),
                 })
        {
            (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
                new AddExpenseRequest(cat, amount, fund, date, note, TripId: tripId))).EnsureSuccessStatusCode();
        }

        var detail = (await client.GetFromJsonAsync<TripDetailDto>($"/accounts/{account.Id}/trips/{tripId}?today=2026-06-21"))!;

        // Newest day first: the late charge (20 Jun), the dinner (12 Jun), then the flight booked back on the 2nd.
        Assert.Equal(new[] { "Late charge", "Dinner", "Flight" }, detail.Expenses.Select(e => e.Note));
        Assert.Equal(new[] { "after", "during", "before" }, detail.Expenses.Select(e => e.When));
        // …and the biggest is still the flight, which is no longer the row the list happens to start with.
        Assert.Equal("Flight", detail.Biggest?.Note);
        Assert.NotEqual(detail.Expenses[0].Note, detail.Biggest?.Note);
        Assert.Equal(298m, detail.Trip.Spent);   // the card's own figures ride along, so one read draws the whole thing
    }

    [Fact]
    public async Task The_split_leads_with_tags_only_when_half_the_trip_is_labelled()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripdetail_axis");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", From, To);
        var tagId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Stay", "house")))
            .Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        // One small labelled row against a big unlabelled one: tags exist, but they describe a tenth of the trip,
        // so leading with them would draw a ring that is 90% hole.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 30m, fund, new DateOnly(2026, 6, 12), "Hostel", TagId: tagId, TripId: tripId))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 270m, fund, new DateOnly(2026, 6, 2), "Flight", TripId: tripId))).EnsureSuccessStatusCode();

        var thin = (await client.GetFromJsonAsync<TripDetailDto>($"/accounts/{account.Id}/trips/{tripId}?today=2026-06-21"))!;
        Assert.Equal("category", thin.SliceAxis);
        Assert.True(thin.HasTagSlices);            // "mostly unlabelled" — a different sentence from "never labelled"

        // Label the big one too and the tag split becomes the honest headline.
        var flightId = thin.Expenses.First(e => e.Note == "Flight").Id;
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{flightId}/tag", new SetExpenseTagRequest(tagId)))
            .EnsureSuccessStatusCode();

        var labelled = (await client.GetFromJsonAsync<TripDetailDto>($"/accounts/{account.Id}/trips/{tripId}?today=2026-06-21"))!;
        Assert.Equal("tag", labelled.SliceAxis);
        var slice = Assert.Single(labelled.Slices);
        Assert.Equal("Stay", slice.Label);
        Assert.Equal(300m, slice.Amount);
        Assert.Equal(2, slice.Count);
    }

    [Fact]
    public async Task A_trip_from_another_account_is_a_404_not_someone_elses_ledger()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripdetail_404");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.GetAsync($"/accounts/{account.Id}/trips/{Guid.NewGuid()}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task The_spending_view_carries_an_expenses_trip_and_tags_plus_the_tag_options()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tripsview_spending");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", From, To);
        var tagId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Restaurants", "utensils")))
            .Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 60m, fund, new DateOnly(2026, 6, 12), "Dinner", TagId: tagId, TripId: tripId)))
            .EnsureSuccessStatusCode();

        var view = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;

        var expense = Assert.Single(view.Expenses);
        Assert.Equal(tripId, expense.TripId);
        Assert.Equal(tagId, Assert.Single(expense.TagIds!));
        var option = Assert.Single(view.TagOptions);
        Assert.Equal("Restaurants", option.Name);
        Assert.False(option.TripTag);
    }

    private static Task<List<ActiveTripDto>?> ActiveTrips(HttpClient client, DateOnly today) =>
        client.GetFromJsonAsync<List<ActiveTripDto>>($"/accounts/active-trips?today={today:yyyy-MM-dd}");

    [Fact]
    public async Task Active_trips_names_every_account_that_is_travelling_and_nothing_else()
    {
        // The account switcher's badge. It cannot be a field on the account list: trips are BODY data, so the
        // question costs a snapshot read per account, and that list is fetched at startup and on every switch.
        var (client, auth) = await _factory.RegisterAndAuthAsync("activetrips_multi");

        var travelling = await CreateAccount(client, "Travelling");
        await SeedAsync(client, travelling.Id, auth.UserId);
        var tripId = await CreateTrip(client, travelling.Id, "Rome", RunFrom, RunTo);

        var homebody = await CreateAccount(client, "Homebody");
        await SeedAsync(client, homebody.Id, auth.UserId);

        // ★ A dated trip nobody has confirmed leaving on is awaiting-start, NOT active — so no badge yet. Same
        // rule the header badge follows, and the reason this reads state rather than comparing dates.
        Assert.Empty((await ActiveTrips(client, Today))!);

        (await client.PutAsJsonAsync($"/accounts/{travelling.Id}/trips/{tripId}/started", new StartTripRequest(true)))
            .EnsureSuccessStatusCode();

        var running = Assert.Single((await ActiveTrips(client, Today))!);
        Assert.Equal(travelling.Id, running.AccountId);
        Assert.Equal("Rome", running.TripName);

        // The account with no journey is simply absent — the response carries rows, not a row per account.
        Assert.DoesNotContain((await ActiveTrips(client, Today))!, t => t.AccountId == homebody.Id);

        // Past the last day it drops out on its own, with no Finish needed.
        Assert.Empty((await ActiveTrips(client, RunTo.AddDays(1)))!);
    }

    [Fact]
    public async Task Active_trips_answers_on_the_callers_own_date_not_the_servers()
    {
        // Whether a trip is running is a question about the traveller's day. A server in UTC would flip it hours
        // early or late for half the world, which is why the date is a parameter here as it is on /trips.
        var (client, auth) = await _factory.RegisterAndAuthAsync("activetrips_today");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id, "Rome", RunFrom, RunTo);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}/started", new StartTripRequest(true)))
            .EnsureSuccessStatusCode();

        Assert.Single((await ActiveTrips(client, RunTo))!);            // last day: still on the road
        Assert.Empty((await ActiveTrips(client, RunTo.AddDays(1)))!);  // one day later: home
    }
}
