using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// T0, past add-expense: every other write that moves money now takes an idempotency key. The failure they exist
/// for is the ordinary one on a bad connection — the request lands, the response is lost, and from the client's
/// side that is indistinguishable from a request that never arrived, so re-sending is all it can do.
///
/// <para>★ Each write gets the same three questions, because a partial answer is the dangerous one: the retry must
/// write <b>nothing</b> (same version — a re-saved identical snapshot would push every other client to re-pull),
/// it must <b>answer like the original</b> (same entity id, or the client treats success as failure and asks the
/// user to try again), and two genuinely separate movements that happen to look alike must both still land.</para>
/// </summary>
public class IdempotentWriteApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public IdempotentWriteApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>An account with the default funds, a savings bucket, an open period and an opening balance to move
    /// about. Returns the Bank fund, a second fund, and the bucket.</summary>
    private static async Task<(Guid Bank, Guid Cash, Guid Bucket)> SeedAsync(
        HttpClient client, Guid accountId, Guid memberId, decimal opening = 1000m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var bank = agg.FundId("Bank");
        var cash = agg.FundId("Cash");
        var bucket = agg.AddSavingCategory("Holiday").Id;
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        if (opening > 0m) period.SetInitialBalance(bank, new Money(opening, "EUR"));
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (bank, cash, bucket);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize(
            (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // ── Income (POST /deposits) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retrying_a_deposit_with_the_same_key_banks_one_salary()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_dep");
        var account = await CreateAccount(client, "Income");
        var (bank, _, _) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();
        var body = new AddDepositRequest(Guid.Empty, bank, 2500m, When, key);

        var first = await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits", body);
        var second = await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits", body);

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.Contributions);
        Assert.Equal(2500m, period.ContributionsPaidTotal.Amount);
        Assert.Equal(first.EntityId, second.EntityId);   // the retry looks like the original's success
        Assert.Equal(first.Version, second.Version);     // ...and wrote nothing
    }

    [Fact]
    public async Task Two_deposits_that_look_alike_both_land_when_their_keys_differ()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_dep_two");
        var account = await CreateAccount(client, "Twice paid");
        var (bank, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        // Two identical payments on one day are a real thing (a split salary, a repeated invoice) — which is why
        // the key is minted per intention and never derived from the contents.
        await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits",
            new AddDepositRequest(Guid.Empty, bank, 400m, When, Guid.NewGuid()));
        await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits",
            new AddDepositRequest(Guid.Empty, bank, 400m, When, Guid.NewGuid()));

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(2, period.Contributions.Count);
        Assert.Equal(800m, period.ContributionsPaidTotal.Amount);
    }

    [Fact]
    public async Task A_deposit_without_a_key_makes_no_claim_about_duplicates()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_dep_none");
        var account = await CreateAccount(client, "Legacy");
        var (bank, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        // Every client that predates T0 sends none and must keep working exactly as it did.
        var body = new AddDepositRequest(Guid.Empty, bank, 100m, When);
        await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits", body);
        await PostAsync<DepositMutationDto>(client, $"/accounts/{account.Id}/deposits", body);

        Assert.Equal(2, (await LoadAsync(client, account.Id)).CurrentPeriod!.Contributions.Count);
    }

    // ── Savings (POST /savings/deposits) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retrying_a_savings_deposit_with_the_same_key_sets_aside_once()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_sav");
        var account = await CreateAccount(client, "Savings");
        var (_, _, bucket) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();
        var body = new AddSavingDepositRequest(bucket, 200m, When, null, key);

        var first = await PostAsync<SavingsMutationDto>(client, $"/accounts/{account.Id}/savings/deposits", body);
        var second = await PostAsync<SavingsMutationDto>(client, $"/accounts/{account.Id}/savings/deposits", body);

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.SavingAllocations);
        Assert.Equal(200m, period.SavingAllocations.Sum(a => a.Amount.Amount));
        Assert.Equal(first.EntityId, second.EntityId);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task A_savings_retry_that_arrives_after_the_amount_was_corrected_still_finds_the_row()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_sav_edit");
        var account = await CreateAccount(client, "Corrected");
        var (_, _, bucket) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();
        var body = new AddSavingDepositRequest(bucket, 200m, When, null, key);

        var added = await PostAsync<SavingsMutationDto>(client, $"/accounts/{account.Id}/savings/deposits", body);

        // The edit REBUILDS the allocation (a new id). If the key did not survive that rebuild, the retry below
        // would find nothing and set aside a second €200 — which is correcting a figure re-opening the very
        // window the key exists to close.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/deposits/{added.EntityId}",
            new EditSavingDepositRequest(250m))).EnsureSuccessStatusCode();

        await PostAsync<SavingsMutationDto>(client, $"/accounts/{account.Id}/savings/deposits", body);

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.SavingAllocations);
        Assert.Equal(250m, period.SavingAllocations.Single().Amount.Amount);   // the correction stands
    }

    // ── Wallet-to-wallet (POST /fund-transfers) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retrying_a_fund_transfer_with_the_same_key_moves_the_money_once()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_ft");
        var account = await CreateAccount(client, "Wallets");
        var (bank, cash, _) = await SeedAsync(client, account.Id, auth.UserId);
        var key = Guid.NewGuid();
        var body = new TransferFundsRequest(bank, cash, 150m, When, "Pocket money", key);

        var first = await PostAsync<FundMutationDto>(client, $"/accounts/{account.Id}/fund-transfers", body);
        var second = await PostAsync<FundMutationDto>(client, $"/accounts/{account.Id}/fund-transfers", body);

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Single(period.FundTransfers);
        Assert.Equal(first.EntityId, second.EntityId);
        Assert.Equal(first.Version, second.Version);
    }

    // ── Account-to-account (POST /transfers-out) — the two-account write ───────────────────────────────────

    [Fact]
    public async Task Retrying_a_transfer_out_moves_money_once_on_BOTH_sides()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_xfer");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (bank, _, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId, opening: 0m);
        var key = Guid.NewGuid();
        var body = new TransferToAccountRequest(dest.Id, bank, 300m, Date: When, ClientId: key);

        var first = await PostAsync<MutationResultDto>(client, $"/accounts/{source.Id}/transfers-out", body);
        var second = await PostAsync<MutationResultDto>(client, $"/accounts/{source.Id}/transfers-out", body);

        // ★ The half a source-side-only check would get wrong: the DESTINATION must not have gained a second
        // deposit either. A transfer is one movement with two ends, so the skip is all-or-nothing.
        Assert.Single((await LoadAsync(client, source.Id)).CurrentPeriod!.ExternalTransfers);
        Assert.Single((await LoadAsync(client, dest.Id)).CurrentPeriod!.Contributions);
        Assert.Equal(first.EntityId, second.EntityId);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task A_second_transfer_of_the_same_amount_lands_when_its_key_differs()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("idem_xfer_two");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");
        var (bank, _, _) = await SeedAsync(client, source.Id, auth.UserId, opening: 1000m);
        await SeedAsync(client, dest.Id, auth.UserId, opening: 0m);

        await PostAsync<MutationResultDto>(client, $"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, bank, 200m, Date: When, ClientId: Guid.NewGuid()));
        await PostAsync<MutationResultDto>(client, $"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, bank, 200m, Date: When, ClientId: Guid.NewGuid()));

        Assert.Equal(2, (await LoadAsync(client, source.Id)).CurrentPeriod!.ExternalTransfers.Count);
        Assert.Equal(2, (await LoadAsync(client, dest.Id)).CurrentPeriod!.Contributions.Count);
    }

    // ── The contract itself ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_request_that_carries_a_key_declares_it_in_the_type_system()
    {
        // ⚠️ The transport retries a body implementing IIdempotentRequest and nothing else, so a request that
        // grows a ClientId without the interface is silently NOT retried — and one that declares the interface
        // while its handler ignores the key is worse. This asserts the first half is checkable by reading the
        // contract rather than by auditing every call site.
        Type[] keyed =
        [
            typeof(AddExpenseRequest), typeof(AddDepositRequest), typeof(AddSavingDepositRequest),
            typeof(TransferFundsRequest), typeof(TransferToAccountRequest),
        ];
        foreach (var t in keyed)
            Assert.True(typeof(IIdempotentRequest).IsAssignableFrom(t), $"{t.Name} carries a key but is not an IIdempotentRequest.");

        var withClientId = typeof(AddExpenseRequest).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetProperty("ClientId") is not null)
            .Where(t => !typeof(IIdempotentRequest).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(withClientId.Count == 0, $"These carry a ClientId but not the interface: {string.Join(", ", withClientId)}");
    }
}
