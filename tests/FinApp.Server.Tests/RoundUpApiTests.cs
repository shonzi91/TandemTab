using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// F4 — the server applies the same round-up sweep the web client applies optimistically. This is the half that
/// matters: the client paints a savings row immediately, and if the server didn't write the identical row the next
/// snapshot refetch would silently take the money back.
/// </summary>
public class RoundUpApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RoundUpApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed an account with a Food category, a savings jar and (optionally) round-ups already switched on.</summary>
    private static async Task<(Guid Category, Guid Fund, Guid Jar)> SeedAsync(
        HttpClient client, Guid accountId, Guid memberId, decimal roundUpTo, decimal deposit = 1000m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var jar = agg.AddSavingCategory("Spare change").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(deposit, "EUR"), fundId: fund);
        if (roundUpTo > 0m) agg.ConfigureRoundUps(roundUpTo, jar);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund, jar);
    }

    private static async Task<Account> ReadBackAsync(HttpClient client, Guid accountId)
    {
        var snap = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!;
        return AccountSnapshotSerializer.Deserialize(snap.Payload);
    }

    [Fact]
    public async Task Adding_an_expense_sweeps_the_change_into_the_configured_bucket()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ru_on");
        var account = await CreateAccount(client, "Round");
        var (cat, fund, jar) = await SeedAsync(client, account.Id, auth.UserId, roundUpTo: 1m);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 12.40m, fund, When, "Lunch"))).EnsureSuccessStatusCode();

        var agg = await ReadBackAsync(client, account.Id);
        var allocations = agg.CurrentPeriod!.SavingAllocations;

        var swept = Assert.Single(allocations);
        Assert.Equal(jar, swept.SavingCategoryId);
        Assert.Equal(0.60m, swept.Amount.Amount);
        // The expense itself is untouched — the round-up is an earmark, not a second charge.
        Assert.Equal(12.40m, Assert.Single(agg.CurrentPeriod.Expenses).Amount.Amount);

        var ov = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;
        Assert.Equal(12.40m, ov.Spent);
        Assert.Equal(987.00m, ov.Free);   // 1000 − 12.40 spent − 0.60 set aside
    }

    [Fact]
    public async Task With_round_ups_off_nothing_is_swept()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ru_off");
        var account = await CreateAccount(client, "Plain");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId, roundUpTo: 0m);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 12.40m, fund, When, "Lunch"))).EnsureSuccessStatusCode();

        var agg = await ReadBackAsync(client, account.Id);
        Assert.Empty(agg.CurrentPeriod!.SavingAllocations);
    }

    [Fact]
    public async Task An_amount_already_on_the_step_sweeps_nothing()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ru_exact");
        var account = await CreateAccount(client, "Exact");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId, roundUpTo: 5m);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 15m, fund, When))).EnsureSuccessStatusCode();

        var agg = await ReadBackAsync(client, account.Id);
        Assert.Empty(agg.CurrentPeriod!.SavingAllocations);
    }

    [Fact]
    public async Task A_sweep_with_no_cash_behind_it_is_skipped_rather_than_failing_the_expense()
    {
        // Deposit exactly what gets spent: the expense must still be recorded, and the change must not be taken.
        var (client, auth) = await _factory.RegisterAndAuthAsync("ru_broke");
        var account = await CreateAccount(client, "Broke");
        var (cat, fund, _) = await SeedAsync(client, account.Id, auth.UserId, roundUpTo: 1m, deposit: 12.40m);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(cat, 12.40m, fund, When))).EnsureSuccessStatusCode();

        var agg = await ReadBackAsync(client, account.Id);
        Assert.Single(agg.CurrentPeriod!.Expenses);
        Assert.Empty(agg.CurrentPeriod.SavingAllocations);
    }

    [Fact]
    public async Task Binding_a_tag_to_a_category_round_trips_through_the_edit_endpoint()
    {
        // F2 — the binding is body data, so the only way it can reach another device is the snapshot the server keeps.
        var (client, auth) = await _factory.RegisterAndAuthAsync("tag_bind");
        var account = await CreateAccount(client, "Tagged");
        var (cat, _, _) = await SeedAsync(client, account.Id, auth.UserId, roundUpTo: 0m);

        var created = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/tags",
            new CreateTagRequest("lidl"))).Content.ReadFromJsonAsync<MutationResultDto>())!;
        var tagId = created.EntityId!.Value;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{tagId}",
            new EditTagRequest("lidl", null, cat))).EnsureSuccessStatusCode();

        Assert.Equal(cat, (await ReadBackAsync(client, account.Id)).FindTag(tagId)!.CategoryId);

        // Sending the tag with no category clears the binding — these requests are a full replace.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{tagId}",
            new EditTagRequest("lidl"))).EnsureSuccessStatusCode();

        Assert.Null((await ReadBackAsync(client, account.Id)).FindTag(tagId)!.CategoryId);
    }
}
