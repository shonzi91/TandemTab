using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Tests;

/// <summary>
/// Spend categories over the wire — specifically the one thing the domain can no longer express.
/// <para>
/// <c>CreateCategoryRequest.ParentId</c> is still on the contract and is ignored. That has to be pinned HERE
/// rather than in the domain, because <c>Account.AddCategory</c> no longer takes a parent at all: it used to, and
/// silently dropped it, which is an offer rather than a refusal — and it is how the phone's category editor came
/// to show a parent picker that changed nothing.
/// </para>
/// </summary>
public class CategoryApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public CategoryApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize(
            (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    [Fact]
    public async Task A_parent_id_from_an_older_client_is_ignored_rather_than_rejected()
    {
        // Ignored, not 400: an older build posting a parent must still succeed in creating the category, because
        // refusing would break category creation entirely on a client that cannot be updated.
        // ⚠️ And it must come back TOP-LEVEL. Honouring it would be worse than ignoring it — the tree is flattened
        // inside AccountSnapshotSerializer.Deserialize, so a sub-category would be turned into a tag the first
        // time the account was read back, silently converting a category the user made into a label.
        var (client, auth) = await _factory.RegisterAndAuthAsync("cat_parent");
        var account = await CreateAccount(client, "Cats");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var food = agg.AddCategory("Food");
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Groceries", ParentId: food.Id));
        resp.EnsureSuccessStatusCode();
        var created = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var loaded = await LoadAsync(client, account.Id);
        var groceries = loaded.Categories.Single(c => c.Id == created);
        Assert.True(groceries.IsRoot);
        Assert.All(loaded.Categories, c => Assert.True(c.IsRoot));
    }

    [Fact]
    public async Task A_category_is_created_with_its_icon_and_essential_flag()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("cat_create");
        var account = await CreateAccount(client, "Cats");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Rent", Icon: "home", Essential: true));
        resp.EnsureSuccessStatusCode();
        var created = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var rent = (await LoadAsync(client, account.Id)).Categories.Single(c => c.Id == created);
        Assert.Equal("Rent", rent.Name);
        Assert.Equal("home", rent.Icon);
        Assert.True(rent.IsEssential);
    }
}
