using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;

namespace FinApp.Server.Tests;

/// <summary>
/// Period lifecycle — roll into the next period (close current + open the next, carrying opening balances),
/// reschedule a period (later periods shift to stay contiguous), and undo the last period (re-opening the previous).
/// Mirrors <c>BudgetingState.StartNextPeriod / ReschedulePeriod / RemoveLatestPeriod</c>. Verified through the
/// snapshot round-trip.
/// </summary>
public class PeriodLifecycleApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public PeriodLifecycleApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Rent" category + an open Dec-2025 period (already ended by 2026), returning
    /// the "Bank" fund id.</summary>
    private static async Task<Guid> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddCategory("Rent");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return agg.FundId("Bank");
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task Start_next_closes_the_current_period_and_opens_the_following_month()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_start");
        var account = await CreateAccount(client, "Roll");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        var newId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(
                CopyBudgets: false,
                FundOpenings: new Dictionary<Guid, decimal> { [fund] = 250m },
                Today: new DateOnly(2026, 1, 15))));

        var loaded = await LoadAsync(client, account.Id);
        Assert.Equal(2, loaded.Periods.Count);
        Assert.Equal(PeriodStatus.Closed, loaded.Periods[0].Status);       // previous period closed

        var next = loaded.Periods[1];
        Assert.Equal(newId, next.Id);
        Assert.Equal(PeriodStatus.Open, next.Status);
        Assert.Equal(new DateOnly(2026, 1, 1), next.From);                 // day after the closed period's To
        Assert.Equal(new DateOnly(2026, 1, 31), next.To);                  // one calendar month
        Assert.Equal(250m, next.InitialBalances.First(b => b.FundId == fund).Amount.Amount);   // opening carried
    }

    [Fact]
    public async Task Start_next_can_copy_the_budgets_forward()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_copy");
        var account = await CreateAccount(client, "Copy");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rent = agg.AddCategory("Rent").Id;
        agg.AddMember(auth.UserId, "Me");
        var p = agg.StartPeriod(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));
        p.AddBudget(rent, new FinApp.Domain.Common.Money(500m, "EUR"));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(CopyBudgets: true, Today: new DateOnly(2026, 1, 15))));

        var next = (await LoadAsync(client, account.Id)).Periods[1];
        Assert.Equal(500m, next.FindBudget(rent)!.Allocated.Amount);
    }

    [Fact]
    public async Task Start_next_is_rejected_while_the_current_period_is_still_running()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_early");
        var account = await CreateAccount(client, "Early");
        await SeedAsync(client, account.Id, auth.UserId);

        // "today" falls inside the Dec-2025 period, which therefore hasn't ended yet.
        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(Today: new DateOnly(2025, 12, 15)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Single((await LoadAsync(client, account.Id)).Periods);   // no new period created
    }

    [Fact]
    public async Task Reschedule_moves_a_period_and_shifts_later_periods_to_stay_contiguous()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_resched");
        var account = await CreateAccount(client, "Resched");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        // Roll forward once so there are two periods; then reschedule the first.
        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(FundOpenings: new Dictionary<Guid, decimal> { [fund] = 0m }, Today: new DateOnly(2026, 1, 15))));

        // Move period 0 from Dec 2025 to a 27-day window; period 1 must shift to start the next day, keeping its length.
        var before = (await LoadAsync(client, account.Id)).Periods[1];
        var lenDays = before.LengthInDays;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/periods/0/schedule",
            new ReschedulePeriodRequest(new DateOnly(2025, 12, 5), new DateOnly(2025, 12, 31)))).EnsureSuccessStatusCode();

        var loaded = await LoadAsync(client, account.Id);
        Assert.Equal(new DateOnly(2025, 12, 5), loaded.Periods[0].From);
        Assert.Equal(new DateOnly(2025, 12, 31), loaded.Periods[0].To);
        Assert.Equal(new DateOnly(2026, 1, 1), loaded.Periods[1].From);          // shifted to stay contiguous
        Assert.Equal(lenDays, loaded.Periods[1].LengthInDays);                    // length preserved
    }

    [Fact]
    public async Task Reschedule_with_an_out_of_range_index_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_badidx");
        var account = await CreateAccount(client, "BadIdx");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/periods/9/schedule",
            new ReschedulePeriodRequest(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Remove_latest_drops_the_last_period_and_reopens_the_previous_one()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_remove");
        var account = await CreateAccount(client, "Remove");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(FundOpenings: new Dictionary<Guid, decimal> { [fund] = 0m }, Today: new DateOnly(2026, 1, 15))));
        Assert.Equal(2, (await LoadAsync(client, account.Id)).Periods.Count);

        var reopenedId = await IdOf(await client.DeleteAsync($"/accounts/{account.Id}/periods/latest"));

        var loaded = await LoadAsync(client, account.Id);
        Assert.Single(loaded.Periods);
        Assert.Equal(reopenedId, loaded.Periods[0].Id);
        Assert.Equal(PeriodStatus.Open, loaded.Periods[0].Status);   // the previous period is editable again
    }

    [Fact]
    public async Task Remove_latest_is_rejected_when_only_one_period_exists()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("pl_onlyone");
        var account = await CreateAccount(client, "OnlyOne");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/periods/latest");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Single((await LoadAsync(client, account.Id)).Periods);
    }

    [Fact]
    public async Task Stranger_cannot_roll_a_period_they_dont_belong_to()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("pl_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("pl_intruder");
        var account = await CreateAccount(owner, "Private");
        await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/periods/start-next",
            new StartNextPeriodRequest(Today: new DateOnly(2026, 1, 15)));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
