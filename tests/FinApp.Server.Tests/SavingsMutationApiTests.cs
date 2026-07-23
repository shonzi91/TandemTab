using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The savings money-movement writes (POST/PUT/DELETE /accounts/{id}/savings/deposits + POST .../savings/spend),
/// mirroring <c>BudgetingState.AllocateSaving/EditSavingDeposit/RemoveSavingDeposit/SpendFromSavings</c>. Confirmed
/// through /overview: a savings deposit raises Saved and lowers Free without moving Current; spending from savings
/// records an expense (Spent up, Current down) and drops the earmark (Saved down).
/// </summary>
public class SavingsMutationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SavingsMutationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Store an initialized snapshot: funds + a "Food" spend category + a "Rainy day" savings bucket + an open
    /// period seeded with a 1000 deposit. Returns the spend-category, savings-bucket and fund ids. Snapshot at v1.</summary>
    private static async Task<(Guid Category, Guid Bucket, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var bucket = agg.AddSavingCategory("Rainy day").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, bucket, fund);
    }

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    [Fact]
    public async Task Add_saving_deposit_raises_saved_and_lowers_free()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_add");
        var account = await CreateAccount(client, "Data");
        var (_, bucket, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 300m, When));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;
        Assert.Equal(2, result.Version);
        Assert.NotEqual(Guid.Empty, result.EntityId!.Value);

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(300m, ov.Saved);
        Assert.Equal(700m, ov.Free);
        Assert.Equal(1000m, ov.Current);   // earmarked, not moved
    }

    [Fact]
    public async Task Edit_saving_deposit_changes_saved()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_edit");
        var account = await CreateAccount(client, "Edit");
        var (_, bucket, _) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 300m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/deposits/{add.EntityId!.Value}",
            new EditSavingDepositRequest(500m))).EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(500m, ov.Saved);
        Assert.Equal(500m, ov.Free);
    }

    [Fact]
    public async Task Remove_saving_deposit_clears_saved()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_del");
        var account = await CreateAccount(client, "Del");
        var (_, bucket, _) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 300m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        (await client.DeleteAsync($"/accounts/{account.Id}/savings/deposits/{add.EntityId!.Value}")).EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(0m, ov.Saved);
        Assert.Equal(1000m, ov.Free);
    }

    [Fact]
    public async Task Spend_from_savings_records_an_expense_and_a_drawdown()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_spend");
        var account = await CreateAccount(client, "Spend");
        var (category, bucket, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // Put 400 aside, then spend 250 of it on Food.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 400m, When))).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/spend",
            new SpendFromSavingsRequest(bucket, category, 250m, When, fund));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;
        Assert.NotEqual(Guid.Empty, result.EntityId!.Value);   // the created expense id

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(250m, ov.Spent);
        Assert.Equal(750m, ov.Current);   // money physically left
        Assert.Equal(150m, ov.Saved);     // 400 earmark − 250 drawdown
        Assert.Equal(600m, ov.Free);      // 750 current − 150 saved
    }

    [Fact]
    public async Task Spend_from_savings_derives_the_default_fund_when_omitted()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_deffund");
        var account = await CreateAccount(client, "DefFund");
        var (category, bucket, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 400m, When))).EnsureSuccessStatusCode();

        // FundId omitted (Guid.Empty) — the server picks the first spendable fund.
        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/spend",
            new SpendFromSavingsRequest(bucket, category, 100m, When));
        resp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(100m, ov.Spent);
    }

    [Fact]
    public async Task Add_saving_deposit_to_unknown_bucket_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_badbucket");
        var account = await CreateAccount(client, "BadBucket");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(Guid.NewGuid(), 100m, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_saving_deposit_with_a_negative_amount_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_neg");
        var account = await CreateAccount(client, "Neg");
        var (_, bucket, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, -50m, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);   // domain: draw down via the spend endpoint
    }

    [Fact]
    public async Task Stranger_cannot_add_a_saving_deposit()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("sv_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("sv_intruder");
        var account = await CreateAccount(owner, "Private");
        var (_, bucket, _) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(bucket, 100m, When));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
