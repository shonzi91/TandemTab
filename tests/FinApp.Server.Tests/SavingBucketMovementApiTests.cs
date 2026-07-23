using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The savings bucket money-movements (POST /savings/disburse, /savings/to-budget, /savings/transfer + DELETE
/// /savings/movements/{id}), mirroring <c>BudgetingState.DisburseSaving/ConvertSavingToBudget/MoveSavingToBucket</c>
/// and the undo. These complete the savings write-surface. Verified through /overview and the snapshot.
/// </summary>
public class SavingBucketMovementApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SavingBucketMovementApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: funds + "Food" category + buckets "A"/"B" + a period with a 1000 deposit and 400 already saved
    /// into bucket A. Returns (food, bucketA, bucketB, fund). Snapshot at v1.</summary>
    private static async Task<(Guid Food, Guid A, Guid B, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var a = agg.AddSavingCategory("A").Id;
        var b = agg.AddSavingCategory("B").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: fund);
        period.AllocateToSavings(a, new Money(400m, "EUR"), When);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, a, b, fund);
    }

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    [Fact]
    public async Task Disburse_sends_money_out_and_drains_the_bucket_without_an_expense()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_disb");
        var account = await CreateAccount(client, "Disb");
        var (_, a, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/disburse",
            new DisburseSavingRequest(a, fund, 250m, When));
        resp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(750m, ov.Current);   // money physically left
        Assert.Equal(150m, ov.Saved);     // 400 earmark − 250 deployed
        Assert.Equal(0m, ov.Spent);       // NOT consumption
        Assert.Equal(600m, ov.Free);
    }

    [Fact]
    public async Task Convert_to_budget_releases_the_earmark_without_moving_money()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_budget");
        var account = await CreateAccount(client, "Budget");
        var (food, a, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/to-budget",
            new ConvertSavingToBudgetRequest(a, food, 200m, When))).EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(1000m, ov.Current);   // no money moved
        Assert.Equal(200m, ov.Saved);      // 400 earmark − 200 released
        Assert.Equal(800m, ov.Free);
        Assert.Equal(0m, ov.Spent);
    }

    [Fact]
    public async Task Move_between_buckets_is_net_neutral()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_move");
        var account = await CreateAccount(client, "Move");
        var (_, a, b, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/transfer",
            new MoveSavingsRequest(a, b, 150m, When))).EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(400m, ov.Saved);   // total earmark unchanged

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(250m, period.SavingAllocations.Where(x => x.SavingCategoryId == a).Sum(x => x.Amount.Amount));
        Assert.Equal(150m, period.SavingAllocations.Where(x => x.SavingCategoryId == b).Sum(x => x.Amount.Amount));
    }

    [Fact]
    public async Task Undo_a_move_to_budget_restores_the_earmark()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_undo");
        var account = await CreateAccount(client, "Undo");
        var (food, a, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/to-budget",
            new ConvertSavingToBudgetRequest(a, food, 200m, When))).EnsureSuccessStatusCode();
        Assert.Equal(200m, (await Overview(client, account.Id))!.Saved);

        // The endpoints don't return the movement's allocation id, so find it on the snapshot (the client lists these too).
        var movementId = (await LoadAsync(client, account.Id)).CurrentPeriod!.SavingMovements().Single().Id;
        (await client.DeleteAsync($"/accounts/{account.Id}/savings/movements/{movementId}")).EnsureSuccessStatusCode();

        Assert.Equal(400m, (await Overview(client, account.Id))!.Saved);   // earmark restored
    }

    [Fact]
    public async Task Disburse_from_an_unknown_bucket_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_badbucket");
        var account = await CreateAccount(client, "BadBucket");
        var (_, _, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/disburse",
            new DisburseSavingRequest(Guid.NewGuid(), fund, 100m, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Moving_to_the_same_bucket_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mv_same");
        var account = await CreateAccount(client, "Same");
        var (_, a, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/transfer",
            new MoveSavingsRequest(a, a, 100m, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_disburse()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("mv_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("mv_intruder");
        var account = await CreateAccount(owner, "Private");
        var (_, a, _, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/savings/disburse",
            new DisburseSavingRequest(a, fund, 100m, When));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
