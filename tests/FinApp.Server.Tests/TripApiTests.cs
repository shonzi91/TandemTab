using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The trip command writes. The theme running through these: a trip is a <b>link</b>, so none of these endpoints may
/// move an expense's date, period or budget impact — and nothing that isn't in this account may be linked to.
/// </summary>
public class TripApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public TripApiTests(FinAppServerFactory factory) => _factory = factory;

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

    private static readonly DateOnly From = new(2026, 6, 10);
    private static readonly DateOnly To = new(2026, 6, 17);

    private static async Task<Guid> CreateTrip(HttpClient client, Guid accountId, string name = "Rome")
    {
        var resp = await client.PostAsJsonAsync($"/accounts/{accountId}/trips",
            new CreateTripRequest(name, From, To, "Rome, Italy", "🇮🇹"));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    private static async Task<Account> Snapshot(HttpClient client, Guid accountId)
    {
        var dto = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!;
        return AccountSnapshotSerializer.Deserialize(dto.Payload);
    }

    [Fact]
    public async Task Creating_a_trip_stores_it_and_advances_the_version()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_create");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);

        var tripId = await CreateTrip(client, account.Id);

        var agg = await Snapshot(client, account.Id);
        var trip = Assert.Single(agg.Trips);
        Assert.Equal(tripId, trip.Id);
        Assert.Equal("Rome", trip.Name);
        Assert.Equal("Rome, Italy", trip.Destination);
        Assert.Equal(From, trip.From);
        Assert.Equal(To, trip.To);
    }

    [Fact]
    public async Task A_trip_that_ends_before_it_starts_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_dates");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/trips",
            new CreateTripRequest("Backwards", To, From));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_trip_name_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_dupe");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);
        await CreateTrip(client, account.Id);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/trips",
            new CreateTripRequest("rome", From, To));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_expense_can_be_logged_straight_onto_a_trip()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_expense");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 60m, fund, new DateOnly(2026, 6, 12), "Dinner", TripId: tripId)))
            .EnsureSuccessStatusCode();

        var agg = await Snapshot(client, account.Id);
        Assert.Single(agg.TripExpenses(tripId));
    }

    [Fact]
    public async Task A_trip_id_that_isnt_in_the_account_is_dropped_rather_than_stored()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_bogus");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 60m, fund, new DateOnly(2026, 6, 12), "Dinner", TripId: Guid.NewGuid())))
            .EnsureSuccessStatusCode();

        var agg = await Snapshot(client, account.Id);
        var expense = Assert.Single(agg.Periods.SelectMany(p => p.Expenses));
        Assert.Null(expense.TripId);   // logged, but not pointing at a trip that doesn't exist
    }

    [Fact]
    public async Task An_existing_expense_can_be_attached_and_detached()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_attach");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);

        var add = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 220m, fund, new DateOnly(2026, 6, 2), "Flight"));
        var expenseId = (await add.Content.ReadFromJsonAsync<ExpenseMutationDto>())!.EntityId;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(tripId))).EnsureSuccessStatusCode();
        Assert.Single((await Snapshot(client, account.Id)).TripExpenses(tripId));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(null))).EnsureSuccessStatusCode();
        Assert.Empty((await Snapshot(client, account.Id)).TripExpenses(tripId));
    }

    [Fact]
    public async Task Attaching_to_a_trip_that_isnt_in_the_account_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_attach_bad");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);

        var add = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 20m, fund, new DateOnly(2026, 6, 2), "Coffee"));
        var expenseId = (await add.Content.ReadFromJsonAsync<ExpenseMutationDto>())!.EntityId;

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}/trip",
            new SetExpenseTripRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Editing_an_expense_keeps_its_trip_even_though_the_request_carries_no_trip_field()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_edit");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);

        var add = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 60m, fund, new DateOnly(2026, 6, 12), "Dinner", TripId: tripId));
        var expenseId = (await add.Content.ReadFromJsonAsync<ExpenseMutationDto>())!.EntityId;

        // The whole point of leaving TripId off EditExpenseRequest: a client that knows nothing about trips can
        // correct an amount without silently dropping the row out of its recap.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}",
            new EditExpenseRequest(cat, 75m, fund, new DateOnly(2026, 6, 12), "Dinner"))).EnsureSuccessStatusCode();

        var agg = await Snapshot(client, account.Id);
        var still = Assert.Single(agg.TripExpenses(tripId));
        Assert.Equal(75m, still.Amount.Amount);
    }

    [Fact]
    public async Task Editing_a_trip_saves_its_budget_savings_link_and_rate()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_edit_meta");
        var account = await CreateAccount(client, "Trips");
        var (_, _, bucket) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}",
            new EditTripRequest("Rome", From, To, "Rome, Italy", "🇮🇹", bucket, 900m, "GBP", 1.17m)))
            .EnsureSuccessStatusCode();

        var trip = Assert.Single((await Snapshot(client, account.Id)).Trips);
        Assert.Equal(bucket, trip.SavingCategoryId);
        Assert.Equal(900m, trip.Budget);
        Assert.Equal("GBP", trip.SpendCurrency);
        Assert.Equal(1.17m, trip.Rate);
    }

    [Fact]
    public async Task Linking_a_savings_bucket_from_another_account_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_bad_bucket");
        var account = await CreateAccount(client, "Trips");
        await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/trips/{tripId}",
            new EditTripRequest("Rome", From, To, SavingCategoryId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_trip_detaches_its_expenses_but_keeps_them()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_delete");
        var account = await CreateAccount(client, "Trips");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId);
        var tripId = await CreateTrip(client, account.Id);
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 60m, fund, new DateOnly(2026, 6, 12), "Dinner", TripId: tripId)))
            .EnsureSuccessStatusCode();

        (await client.DeleteAsync($"/accounts/{account.Id}/trips/{tripId}")).EnsureSuccessStatusCode();

        var agg = await Snapshot(client, account.Id);
        Assert.Empty(agg.Trips);
        var expense = Assert.Single(agg.Periods.SelectMany(p => p.Expenses));
        Assert.Null(expense.TripId);          // detached
        Assert.Equal(60m, expense.Amount.Amount);   // the money was still spent
    }

    [Fact]
    public async Task Trip_tags_are_seeded_once_and_a_second_call_in_another_language_adds_nothing()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("trip_tags");
        var account = await CreateAccount(client, "Trips");
        var (cat, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/trip-tags", new SeedTripTagsRequest(
            [new TripTagSeed("Stay", "🏨", cat), new TripTagSeed("Travel", "✈️", null)]))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/trip-tags", new SeedTripTagsRequest(
            [new TripTagSeed("Настаняване", "🏨", null), new TripTagSeed("Транспорт", "✈️", null)]))).EnsureSuccessStatusCode();

        var agg = await Snapshot(client, account.Id);
        Assert.Equal(2, agg.TripTags.Count());
        Assert.Equal(2, agg.Tags.Count);   // no parallel Bulgarian set
        Assert.Equal(cat, agg.TripTags.First(t => t.Name == "Stay").CategoryId);
    }

    [Fact]
    public async Task Someone_elses_account_cannot_have_a_trip_added_to_it()
    {
        var (owner, ownerAuth) = await _factory.RegisterAndAuthAsync("trip_owner");
        var account = await CreateAccount(owner, "Private");
        await SeedAsync(owner, account.Id, ownerAuth.UserId);

        var (stranger, _) = await _factory.RegisterAndAuthAsync("trip_stranger");
        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/trips",
            new CreateTripRequest("Sneak", From, To));

        Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }
}
