using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Account-to-account transfers as ONE movement: the outflow here and the deposit it created there are edited and
/// removed together, addressed by the pair id both rows carry. The tests that matter most are the two ends of the
/// promise — no money is stranded on one side, and a transfer written before the link existed still behaves.
/// </summary>
public class AccountTransferPairTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AccountTransferPairTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<Guid> SeedAsync(HttpClient client, Guid accountId, Guid memberId, decimal opening = 0m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        if (opening > 0m) period.SetInitialBalance(fund, new Money(opening, "EUR"));
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return fund;
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    /// <summary>Make a transfer and return (sourceFund, pairId).</summary>
    private static async Task<(Guid Fund, Guid PairId)> TransferAsync(HttpClient client, Guid source, Guid dest, Guid fund, decimal amount, string? note = null)
    {
        (await client.PostAsJsonAsync($"/accounts/{source}/transfers-out",
            new TransferToAccountRequest(dest, fund, amount, Note: note))).EnsureSuccessStatusCode();
        var outflow = (await LoadAsync(client, source)).CurrentPeriod!.ExternalTransfers.Last();
        return (fund, outflow.AccountTransferId!.Value);
    }

    [Fact]
    public async Task Both_halves_are_stamped_with_the_same_pair_id()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xp_link");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId);

        var (_, pairId) = await TransferAsync(client, source.Id, dest.Id, fund, 300m);

        var deposit = (await LoadAsync(client, dest.Id)).CurrentPeriod!.Contributions.Single(c => c.AccountTransferId == pairId);
        Assert.Equal(source.Id, deposit.FromAccountId);
        Assert.True(deposit.IsTransferIn);
    }

    [Fact]
    public async Task Editing_changes_the_amount_on_both_sides()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xp_edit");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId);
        var (_, pairId) = await TransferAsync(client, source.Id, dest.Id, fund, 300m, "rent");

        (await client.PutAsJsonAsync($"/accounts/{source.Id}/account-transfers/{pairId}",
            new EditAccountTransferRequest(dest.Id, 450m, fund, Note: "rent + bills",
                Date: new DateOnly(2026, 1, 20)))).EnsureSuccessStatusCode();

        var sourcePeriod = (await LoadAsync(client, source.Id)).CurrentPeriod!;
        var outflow = sourcePeriod.ExternalTransfers.Single();
        Assert.Equal(450m, outflow.Amount.Amount);
        Assert.Equal("rent + bills", outflow.Note);
        Assert.Equal(new DateOnly(2026, 1, 20), outflow.Date);
        Assert.Equal(550m, sourcePeriod.FundBalance(fund).Amount);   // 1000 opening − 450 out

        var deposit = (await LoadAsync(client, dest.Id)).CurrentPeriod!.Contributions.Single(c => c.AccountTransferId == pairId);
        Assert.Equal(450m, deposit.Paid.Amount);
        Assert.Equal(new DateOnly(2026, 1, 20), deposit.Date);
    }

    /// <summary>Raising a transfer must measure headroom with its OWN amount added back, or the fund looks poorer
    /// than it is by exactly the figure being replaced.</summary>
    [Fact]
    public async Task Raising_a_transfer_up_to_the_funds_full_balance_is_allowed()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xp_raise");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 500m);
        await SeedAsync(client, dest.Id, auth.UserId);
        var (_, pairId) = await TransferAsync(client, source.Id, dest.Id, fund, 400m);   // 100 left in the fund

        var ok = await client.PutAsJsonAsync($"/accounts/{source.Id}/account-transfers/{pairId}",
            new EditAccountTransferRequest(dest.Id, 500m, fund));
        ok.EnsureSuccessStatusCode();
        Assert.Equal(0m, (await LoadAsync(client, source.Id)).CurrentPeriod!.FundBalance(fund).Amount);

        var tooMuch = await client.PutAsJsonAsync($"/accounts/{source.Id}/account-transfers/{pairId}",
            new EditAccountTransferRequest(dest.Id, 501m, fund));
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
    }

    [Fact]
    public async Task Removing_takes_both_the_outflow_and_the_deposit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xp_del");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId);
        var (_, pairId) = await TransferAsync(client, source.Id, dest.Id, fund, 300m);

        (await client.DeleteAsync($"/accounts/{source.Id}/account-transfers/{pairId}?destinationAccountId={dest.Id}"))
            .EnsureSuccessStatusCode();

        var sourcePeriod = (await LoadAsync(client, source.Id)).CurrentPeriod!;
        Assert.Empty(sourcePeriod.ExternalTransfers);
        Assert.Equal(1000m, sourcePeriod.FundBalance(fund).Amount);   // the outflow is reversed
        Assert.DoesNotContain((await LoadAsync(client, dest.Id)).CurrentPeriod!.Contributions,
            c => c.AccountTransferId == pairId);
    }

    /// <summary>
    /// The migration case. A snapshot written before the link existed has no AccountTransferId on either half; it
    /// must still load, keep every figure, and simply not be addressable as a pair (rather than matching the wrong
    /// deposit or failing to deserialize).
    /// </summary>
    [Fact]
    public async Task A_transfer_from_before_the_link_existed_still_loads_and_is_not_pairable()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xp_legacy");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        await SeedAsync(client, dest.Id, auth.UserId);

        // Build the old shape by hand: an outflow and a deposit with no pair id, exactly as pre-link data reads.
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(fund, new Money(1000m, "EUR"));
        period.TransferOut(fund, new Money(250m, "EUR"), new DateOnly(2026, 1, 9), dest.Id, "old money");
        (await client.PutAsJsonAsync($"/accounts/{source.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var loaded = (await LoadAsync(client, source.Id)).CurrentPeriod!;
        var legacy = Assert.Single(loaded.ExternalTransfers);
        Assert.Null(legacy.AccountTransferId);            // not invented on read
        Assert.Equal(250m, legacy.Amount.Amount);         // and nothing else is disturbed
        Assert.Equal("old money", legacy.Note);
        Assert.Equal(dest.Id, legacy.ToAccountId);
        Assert.Equal(750m, loaded.FundBalance(fund).Amount);

        // Its row id is not a pair id, so the two-sided routes decline rather than touching an unrelated deposit.
        var resp = await client.DeleteAsync($"/accounts/{source.Id}/account-transfers/{legacy.Id}?destinationAccountId={dest.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Single((await LoadAsync(client, source.Id)).CurrentPeriod!.ExternalTransfers);
    }

    [Fact]
    public async Task A_stranger_cannot_edit_a_pair_between_accounts_they_dont_belong_to()
    {
        var (owner, ownerAuth) = await _factory.RegisterAndAuthAsync("xp_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("xp_stranger");
        var source = await CreateAccount(owner, "Source");
        var dest = await CreateAccount(owner, "Dest");
        var fund = await SeedAsync(owner, source.Id, ownerAuth.UserId, opening: 1000m);
        await SeedAsync(owner, dest.Id, ownerAuth.UserId);
        var (_, pairId) = await TransferAsync(owner, source.Id, dest.Id, fund, 300m);

        var resp = await stranger.PutAsJsonAsync($"/accounts/{source.Id}/account-transfers/{pairId}",
            new EditAccountTransferRequest(dest.Id, 1m, fund));
        Assert.True(resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"expected the stranger to be refused, got {resp.StatusCode}");
        Assert.Equal(300m, (await LoadAsync(owner, source.Id)).CurrentPeriod!.ExternalTransfers.Single().Amount.Amount);
    }
}
