using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Tests;

/// <summary>
/// Fund transfers + opening balances — intra-account money placement on the open period. Move money between the
/// account's own funds (total-preserving, so the source may go negative) and set a fund's opening balance. Mirrors
/// <c>BudgetingState.TransferFunds/EditFundTransfer/RemoveFundTransfer/SetFundOpeningBalance</c>. Verified via the
/// snapshot round-trip (per-fund balances + the transfer ledger).
/// </summary>
public class FundTransferApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public FundTransferApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds ("Bank" + "Cash") + an open Jan-2026 period. Returns (bank, cash) fund ids.</summary>
    private static async Task<(Guid Bank, Guid Cash)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (agg.FundId("Bank"), agg.FundId("Cash"));
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task Set_opening_balance_records_it_for_the_fund()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_open");
        var account = await CreateAccount(client, "Open");
        var (bank, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{bank}/opening-balance",
            new SetFundOpeningBalanceRequest(1200m))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(1200m, period.InitialBalances.First(b => b.FundId == bank).Amount.Amount);
        Assert.Equal(1200m, period.InitialTotal.Amount);   // real (non-informative) opening counts
    }

    [Fact]
    public async Task Set_opening_balance_overwrites_a_previous_value()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_openupd");
        var account = await CreateAccount(client, "OpenUpd");
        var (bank, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{bank}/opening-balance", new SetFundOpeningBalanceRequest(1000m))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{bank}/opening-balance", new SetFundOpeningBalanceRequest(1500m))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.InitialBalances, b => b.FundId == bank);   // overwritten, not appended
        Assert.Equal(1500m, period.InitialBalances.First(b => b.FundId == bank).Amount.Amount);
    }

    [Fact]
    public async Task Transfer_moves_money_between_funds_preserving_the_total()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_move");
        var account = await CreateAccount(client, "Move");
        var (bank, cash) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{bank}/opening-balance", new SetFundOpeningBalanceRequest(1000m))).EnsureSuccessStatusCode();

        var transferId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, cash, 300m, new DateOnly(2026, 1, 10), "top up cash")));

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(700m, period.FundBalance(bank).Amount);    // 1000 - 300
        Assert.Equal(300m, period.FundBalance(cash).Amount);    // 0 + 300
        var t = period.FundTransfers.First(x => x.Id == transferId);
        Assert.Equal("top up cash", t.Note);
        Assert.Equal(new DateOnly(2026, 1, 10), t.Date);
    }

    [Fact]
    public async Task Transfer_may_take_the_source_fund_negative()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_neg");
        var account = await CreateAccount(client, "Neg");
        var (bank, cash) = await SeedAsync(client, account.Id, auth.UserId);

        // No opening balance anywhere — intra-account moves aren't capped, so this is allowed.
        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, cash, 50m)));

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(-50m, period.FundBalance(bank).Amount);
        Assert.Equal(50m, period.FundBalance(cash).Amount);
    }

    [Fact]
    public async Task Edit_transfer_changes_the_amount_and_keeps_the_original_date()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_edit");
        var account = await CreateAccount(client, "Edit");
        var (bank, cash) = await SeedAsync(client, account.Id, auth.UserId);

        var transferId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, cash, 300m, new DateOnly(2026, 1, 10))));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/fund-transfers/{transferId}",
            new EditFundTransferRequest(bank, cash, 450m, "revised"))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        var t = period.FundTransfers.Single();
        Assert.Equal(450m, t.Amount.Amount);
        Assert.Equal("revised", t.Note);
        Assert.Equal(new DateOnly(2026, 1, 10), t.Date);   // original date preserved through the edit
        Assert.Equal(-450m, period.FundBalance(bank).Amount);
    }

    [Fact]
    public async Task Remove_transfer_restores_the_balances()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_remove");
        var account = await CreateAccount(client, "Rm");
        var (bank, cash) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{bank}/opening-balance", new SetFundOpeningBalanceRequest(1000m))).EnsureSuccessStatusCode();
        var transferId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, cash, 300m)));

        (await client.DeleteAsync($"/accounts/{account.Id}/fund-transfers/{transferId}")).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Empty(period.FundTransfers);
        Assert.Equal(1000m, period.FundBalance(bank).Amount);   // back to the opening
        Assert.Equal(0m, period.FundBalance(cash).Amount);
    }

    [Fact]
    public async Task Transfer_between_the_same_fund_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_same");
        var account = await CreateAccount(client, "Same");
        var (bank, _) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, bank, 100m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Transfer_from_an_unknown_fund_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_badfund");
        var account = await CreateAccount(client, "BadFund");
        var (_, cash) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(Guid.NewGuid(), cash, 100m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Opening_balance_on_an_unknown_fund_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("ft_badopen");
        var account = await CreateAccount(client, "BadOpen");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{Guid.NewGuid()}/opening-balance",
            new SetFundOpeningBalanceRequest(500m));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_transfer_funds()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("ft_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("ft_intruder");
        var account = await CreateAccount(owner, "Private");
        var (bank, cash) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/fund-transfers",
            new TransferFundsRequest(bank, cash, 100m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
