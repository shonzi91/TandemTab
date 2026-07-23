using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The income (deposit) command writes (POST/PUT/DELETE /accounts/{id}/deposits), mirroring
/// <c>BudgetingState.RecordDeposit/EditDeposit/RemoveDeposit</c> — including the merge-by-(member,category,fund)
/// semantics, general income (empty category), and the "own deposits only" guard (403). Confirmed through /overview's
/// Contributed figure.
/// </summary>
public class DepositMutationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public DepositMutationApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Store an initialized snapshot (funds + a "Salary" contribution category + an open period, no deposit)
    /// and return the contribution-category and fund ids the requests reference. Snapshot starts at version 1.</summary>
    private static async Task<(Guid Salary, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var salary = agg.AddContributionCategory("Salary").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (salary, fund);
    }

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    [Fact]
    public async Task Add_deposit_advances_the_version_and_shows_in_contributed()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_add");
        var account = await CreateAccount(client, "Data");
        var (salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 500m, When));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;

        Assert.Equal(2, result.Version);
        Assert.NotEqual(Guid.Empty, result.EntityId!.Value);

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(500m, ov.Contributed);
        Assert.Equal(500m, ov.Current);
    }

    [Fact]
    public async Task Deposits_with_the_same_member_category_fund_merge_into_one_row()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_merge");
        var account = await CreateAccount(client, "Merge");
        var (salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var first = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 300m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;
        var second = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 200m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        Assert.Equal(first.EntityId, second.EntityId);   // same row, not a new one
        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(500m, ov.Contributed);
    }

    [Fact]
    public async Task General_income_with_an_empty_category_is_accepted()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_general");
        var account = await CreateAccount(client, "General");
        var (_, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(Guid.Empty, fund, 250m, When));
        resp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(250m, ov.Contributed);
    }

    [Fact]
    public async Task Edit_deposit_replaces_its_amount()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_edit");
        var account = await CreateAccount(client, "Edit");
        var (salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 500m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        var editResp = await client.PutAsJsonAsync($"/accounts/{account.Id}/deposits/{add.EntityId!.Value}",
            new EditDepositRequest(salary, fund, 800m, When));
        editResp.EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(800m, ov.Contributed);
    }

    [Fact]
    public async Task Remove_deposit_clears_contributed()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_del");
        var account = await CreateAccount(client, "Del");
        var (salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var add = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 500m, When))).Content.ReadFromJsonAsync<MutationResultDto>())!;

        (await client.DeleteAsync($"/accounts/{account.Id}/deposits/{add.EntityId!.Value}")).EnsureSuccessStatusCode();

        var ov = (await Overview(client, account.Id))!;
        Assert.Equal(0m, ov.Contributed);
    }

    [Fact]
    public async Task Add_deposit_with_unknown_category_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_badcat");
        var account = await CreateAccount(client, "BadCat");
        var (_, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(Guid.NewGuid(), fund, 100m, When));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_edit_another_members_deposit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dep_owner");
        var account = await CreateAccount(client, "Shared");

        // Seed a deposit that belongs to a DIFFERENT member, and capture its id.
        var other = Guid.NewGuid();
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var salary = agg.AddContributionCategory("Salary").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(auth.UserId, "Me");
        agg.AddMember(other, "Them");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var theirs = period.Deposit(other, new Money(400m, "EUR"), salary, fund, When).Id;
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var editResp = await client.PutAsJsonAsync($"/accounts/{account.Id}/deposits/{theirs}",
            new EditDepositRequest(salary, fund, 999m, When));
        Assert.Equal(HttpStatusCode.Forbidden, editResp.StatusCode);

        var delResp = await client.DeleteAsync($"/accounts/{account.Id}/deposits/{theirs}");
        Assert.Equal(HttpStatusCode.Forbidden, delResp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_add_a_deposit()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("dep_ownr2");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("dep_intruder");
        var account = await CreateAccount(owner, "Private");
        var (salary, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(salary, fund, 100m, When));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
