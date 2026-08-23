using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Tests;

/// <summary>
/// A wallet that holds foreign cash, as the <b>thin</b> clients see it.
/// <para>The write side was always complete — <c>AddExpenseRequest</c> has carried <c>ForeignAmount</c> and
/// <c>ForeignCurrency</c> since the feature shipped, and the server stores what it is given without re-converting.
/// The gap was the <b>read</b>: no thin contract carried a fund's currency or rate anywhere, so a client that is
/// never told the rate cannot convert by it. The phone stored 100 kr as 100 EUR — face value, in the one situation
/// the feature exists for.</para>
/// <para>These pin the two reads an entry form actually uses: the wallets row, and the fund <i>option</i> the
/// picker is built from. The option matters more of the two — picking the wallet is what changes the meaning of
/// the Amount field.</para>
/// </summary>
public class ForeignWalletApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ForeignWalletApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>A EUR account whose "Cash" wallet holds Swedish kronor at 1 SEK = 0.087 EUR, and whose "Bank"
    /// wallet is an ordinary one. Returns (cash, bank).</summary>
    private static async Task<(Guid Cash, Guid Bank)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var cash = agg.FundId("Cash");
        agg.SetFundCurrency(cash, "SEK", 0.087m);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (cash, agg.FundId("Bank"));
    }

    [Fact]
    public async Task The_wallets_row_carries_the_currency_and_the_rate()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("fw_row");
        var account = await CreateAccount(client, "Foreign");
        var (cash, bank) = await SeedAsync(client, account.Id, auth.UserId);

        var view = (await client.GetFromJsonAsync<WalletsViewDto>($"/accounts/{account.Id}/wallets"))!;
        var foreign = view.Funds.Single(f => f.Id == cash);
        var ordinary = view.Funds.Single(f => f.Id == bank);

        Assert.Equal("SEK", foreign.Currency);
        Assert.Equal(0.087m, foreign.Rate);

        // An ordinary wallet says nothing rather than saying the account's own currency — a client must be able to
        // tell "no conversion here" from "converts at 1.0", and only null does that unambiguously.
        Assert.Null(ordinary.Currency);
        Assert.Null(ordinary.Rate);
    }

    [Fact]
    public async Task The_fund_picker_option_carries_them_too()
    {
        // The row is not enough: the add-expense sheet is built from the OPTION list, and it is choosing the wallet
        // that changes what the Amount field means.
        var (client, auth) = await _factory.RegisterAndAuthAsync("fw_option");
        var account = await CreateAccount(client, "Foreign");
        var (cash, bank) = await SeedAsync(client, account.Id, auth.UserId);

        var view = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        var foreign = view.Funds.Single(f => f.Id == cash);

        Assert.Equal("SEK", foreign.Currency);
        Assert.Equal(0.087m, foreign.Rate);
        Assert.Null(view.Funds.Single(f => f.Id == bank).Currency);
    }

    [Fact]
    public async Task A_currency_with_no_rate_is_sent_as_a_currency_with_no_rate()
    {
        // The two fields are independent on the wire but not in meaning: the domain's HasRate requires both, and a
        // wallet can legitimately be labelled with a currency before anyone has typed a rate. The contract must not
        // quietly invent 1.0 for the missing half — a client that converted by it would store kronor as euros and
        // look like it had done the arithmetic.
        var (client, auth) = await _factory.RegisterAndAuthAsync("fw_norate");
        var account = await CreateAccount(client, "Foreign");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var cash = agg.FundId("Cash");
        agg.SetFundCurrency(cash, "SEK", null);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var fund = (await client.GetFromJsonAsync<WalletsViewDto>($"/accounts/{account.Id}/wallets"))!
            .Funds.Single(f => f.Id == cash);

        Assert.Equal("SEK", fund.Currency);
        Assert.Null(fund.Rate);
    }
}
