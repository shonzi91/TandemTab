using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Path-B thin Structure read (<c>GET /accounts/{id}/structure</c>): the account's spend categories, funds and
/// contribution categories with their icons, hierarchy and archived/essential/synced flags. The create/edit/archive
/// commands are exercised in <see cref="StructureCrudApiTests"/>; this asserts the read model reflects them.
/// </summary>
public class StructureViewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public StructureViewApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Food" spend category. Returns (food, bankFund).</summary>
    private static async Task<(Guid Food, Guid Bank)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, bank);
    }

    private static Task<StructureViewDto?> GetStructure(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<StructureViewDto>($"/accounts/{accountId}/structure");

    [Fact]
    public async Task Structure_lists_seeded_categories_and_funds()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_seed");
        var account = await CreateAccount(client, "View");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        var view = (await GetStructure(client, account.Id))!;

        Assert.Contains(view.Categories, c => c.Id == food && c.Name == "Food");
        // Default funds include "Bank" and "Cash"; none are bank-synced on a fresh account.
        Assert.Contains(view.Funds, f => f.Name == "Bank");
        Assert.All(view.Funds, f => Assert.False(f.Synced));
    }

    [Fact]
    public async Task Created_child_category_carries_parent_icon_and_essential()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_child");
        var account = await CreateAccount(client, "Child");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        var created = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Groceries", ParentId: food, Icon: "cart", Essential: true)))
            .Content.ReadFromJsonAsync<MutationResultDto>())!;

        var row = (await GetStructure(client, account.Id))!.Categories.Single(c => c.Id == created.EntityId);
        Assert.Equal("Groceries", row.Name);
        Assert.Equal("cart", row.Icon);
        Assert.Equal(food, row.ParentId);
        Assert.True(row.Essential);
    }

    [Fact]
    public async Task Archived_category_shows_in_the_view_with_the_flag_set()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_arch");
        var account = await CreateAccount(client, "Arch");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/categories/{food}/archived",
            new SetArchivedRequest(true))).EnsureSuccessStatusCode();

        var row = (await GetStructure(client, account.Id))!.Categories.Single(c => c.Id == food);
        Assert.True(row.Archived);
    }

    [Fact]
    public async Task Contribution_category_appears_after_creation()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sv_cc");
        var account = await CreateAccount(client, "CC");
        await SeedAsync(client, account.Id, auth.UserId);

        var created = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/contribution-categories",
            new CreateContributionCategoryRequest("Salary", Icon: "wage")))
            .Content.ReadFromJsonAsync<MutationResultDto>())!;

        var row = (await GetStructure(client, account.Id))!.ContributionCategories.Single(c => c.Id == created.EntityId);
        Assert.Equal("Salary", row.Name);
        Assert.Equal("wage", row.Icon);
    }
}
