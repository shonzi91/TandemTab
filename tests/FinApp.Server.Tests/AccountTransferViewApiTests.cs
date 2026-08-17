using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The account-to-account transfer <b>read</b> model. Three endpoints could create, edit and delete one, but nothing
/// returned a transfer — so a thin client had no row to carry the pair id the edit and delete are addressed by, and
/// the commands were reachable only by a client that already knew an id it had no way to learn.
/// </summary>
public class AccountTransferViewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AccountTransferViewApiTests(FinAppServerFactory factory) => _factory = factory;

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

    private static Task<WalletsViewDto?> Wallets(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<WalletsViewDto>($"/accounts/{accountId}/wallets");

    [Fact]
    public async Task A_transfer_out_is_listed_with_the_pair_id_its_edit_needs()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("acctxview_listed");
        var source = await CreateAccount(client, "Main");
        var dest = await CreateAccount(client, "Joint");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 500m);
        await SeedAsync(client, dest.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, fund, 120m, Note: "Rent share"))).EnsureSuccessStatusCode();

        var row = Assert.Single((await Wallets(client, source.Id))!.AccountTransferRows);

        Assert.Equal(120m, row.Amount);
        Assert.Equal("Rent share", row.Note);
        Assert.Equal(dest.Id, row.ToAccountId);
        Assert.Equal("Joint", row.ToAccountName);   // resolved from the caller's own accounts
        Assert.NotNull(row.PairId);
        Assert.True(row.Editable);

        // And the pair id it carries is the one the edit endpoint actually accepts.
        (await client.PutAsJsonAsync($"/accounts/{source.Id}/account-transfers/{row.PairId}",
            new EditAccountTransferRequest(dest.Id, 75m))).EnsureSuccessStatusCode();
        Assert.Equal(75m, Assert.Single((await Wallets(client, source.Id))!.AccountTransferRows).Amount);
    }

    /// <summary>
    /// A fund-to-fund move and an account-to-account move are different things — one keeps the money, the other
    /// sends it away — so listing them together would make the wallets total unexplainable.
    /// </summary>
    [Fact]
    public async Task A_move_between_wallets_is_not_an_account_transfer()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("acctxview_internal");
        var account = await CreateAccount(client, "Main");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var from = agg.FundId("Bank");
        var to = agg.FundId("Cash");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(from, new Money(500m, "EUR"));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(from, to, 50m, new DateOnly(2026, 1, 12)))).EnsureSuccessStatusCode();

        var view = (await Wallets(client, account.Id))!;
        Assert.Single(view.Transfers);              // the internal move
        Assert.Empty(view.AccountTransferRows);     // and nothing in the outbound list
    }

    /// <summary>The destination name comes from the caller's own accounts, so a transfer to one they can no longer
    /// see still lists — the money left either way, and hiding the row would make the balance unexplainable.</summary>
    [Fact]
    public async Task A_transfer_to_an_account_the_caller_cannot_see_still_lists_without_a_name()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("acctxview_unknown");
        var source = await CreateAccount(client, "Main");
        var fund = await SeedAsync(client, source.Id, auth.UserId, opening: 500m);

        // Written straight into the aggregate, pointing at an account this user has no membership in.
        var agg = AccountSnapshotSerializer.Deserialize(
            (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{source.Id}/snapshot"))!.Payload);
        agg.CurrentPeriod!.TransferOut(fund, new Money(60m, "EUR"), new DateOnly(2026, 1, 20), Guid.NewGuid(), "Gone");
        var version = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{source.Id}/snapshot"))!.Version;
        (await client.PutAsJsonAsync($"/accounts/{source.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), version))).EnsureSuccessStatusCode();

        var row = Assert.Single((await Wallets(client, source.Id))!.AccountTransferRows);

        Assert.Equal(60m, row.Amount);
        Assert.Null(row.ToAccountName);
        // No pair id was ever written, so the two-sided edit cannot address it — and the row says so.
        Assert.Null(row.PairId);
        Assert.False(row.Editable);
    }
}
