using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Account-structure CRUD — spend categories, funds, and contribution (income) categories (create/edit/archive/remove),
/// mirroring the client's <c>Add*/Edit*/Archive*/Remove*</c>. Verified by deserializing the snapshot; removal blockers
/// are exercised with a real referencing row created through the expense/deposit endpoints.
/// </summary>
public class StructureCrudApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public StructureCrudApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Food" spend category + an open period with a 1000 deposit. Returns (food, bankFund).</summary>
    private static async Task<(Guid Food, Guid Bank)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: bank);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, bank);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    // --- Spend categories ----------------------------------------------------

    [Fact]
    public async Task Create_category_keeps_icon_and_essential_and_ignores_any_parent()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cat_create");
        var account = await CreateAccount(client, "Data");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        var childId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Groceries", ParentId: food, Icon: "cart", Essential: true)));

        var child = (await LoadAsync(client, account.Id)).FindCategory(childId)!;
        Assert.Equal("Groceries", child.Name);
        Assert.Equal("cart", child.Icon);
        Assert.Null(child.ParentId);   // categories are flat now — see CategoryFlattenTests
        Assert.True(child.IsEssential);
    }

    [Fact]
    public async Task Edit_category_renames_sets_icon_and_toggles_essential()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cat_edit");
        var account = await CreateAccount(client, "Edit");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/categories/{food}",
            new EditCategoryRequest("Dining", Icon: "fork", Essential: true))).EnsureSuccessStatusCode();

        var cat = (await LoadAsync(client, account.Id)).FindCategory(food)!;
        Assert.Equal("Dining", cat.Name);
        Assert.Equal("fork", cat.Icon);
        Assert.True(cat.IsEssential);
    }

    [Fact]
    public async Task Archive_and_restore_category()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cat_arch");
        var account = await CreateAccount(client, "Arch");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/categories/{food}/archived", new SetArchivedRequest(true))).EnsureSuccessStatusCode();
        Assert.True((await LoadAsync(client, account.Id)).FindCategory(food)!.IsArchived);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/categories/{food}/archived", new SetArchivedRequest(false))).EnsureSuccessStatusCode();
        Assert.False((await LoadAsync(client, account.Id)).FindCategory(food)!.IsArchived);
    }

    [Fact]
    public async Task Remove_unused_category_and_reject_a_duplicate_name()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cat_rm");
        var account = await CreateAccount(client, "Rm");
        await SeedAsync(client, account.Id, auth.UserId);

        var tempId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Temp")));
        (await client.DeleteAsync($"/accounts/{account.Id}/categories/{tempId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindCategory(tempId));

        // "Food" already exists from the seed → duplicate.
        var dup = await client.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Food"));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    [Fact]
    public async Task Remove_category_is_blocked_by_a_referencing_expense()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cat_block");
        var account = await CreateAccount(client, "Block");
        var (food, bank) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 20m, bank, When))).EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/categories/{food}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --- Funds ---------------------------------------------------------------

    [Fact]
    public async Task Create_and_edit_a_fund()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_fund_edit");
        var account = await CreateAccount(client, "Fund");
        await SeedAsync(client, account.Id, auth.UserId);

        var fundId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/funds",
            new CreateFundRequest("Savings jar", Note: "coins", Icon: "jar")));
        var created = (await LoadAsync(client, account.Id)).FindFund(fundId)!;
        Assert.Equal("Savings jar", created.Name);
        Assert.Equal("coins", created.Note);
        Assert.Equal("jar", created.Icon);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{fundId}",
            new EditFundRequest("Coin jar", Note: null, Icon: "coins"))).EnsureSuccessStatusCode();
        var edited = (await LoadAsync(client, account.Id)).FindFund(fundId)!;
        Assert.Equal("Coin jar", edited.Name);
        Assert.Null(edited.Note);   // cleared
        Assert.Equal("coins", edited.Icon);
    }

    [Fact]
    public async Task Archive_a_fund_then_remove_an_unused_one()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_fund_arch");
        var account = await CreateAccount(client, "FundArch");
        await SeedAsync(client, account.Id, auth.UserId);

        var fundId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/funds", new CreateFundRequest("Spare")));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/funds/{fundId}/archived", new SetArchivedRequest(true))).EnsureSuccessStatusCode();
        Assert.True((await LoadAsync(client, account.Id)).FindFund(fundId)!.IsArchived);

        (await client.DeleteAsync($"/accounts/{account.Id}/funds/{fundId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindFund(fundId));
    }

    [Fact]
    public async Task Remove_fund_moves_its_opening_balance_to_the_target()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_fund_movebal");
        var account = await CreateAccount(client, "MoveBal");

        // Seed a fund ("Cash") holding a 500 opening balance; remove it, moving the balance to "Bank".
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var bank = agg.FundId("Bank");
        var cash = agg.FundId("Cash");
        agg.AddMember(auth.UserId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(cash, new Money(500m, "EUR"));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        (await client.DeleteAsync($"/accounts/{account.Id}/funds/{cash}?moveOpeningBalancesTo={bank}")).EnsureSuccessStatusCode();

        var loaded = await LoadAsync(client, account.Id);
        Assert.Null(loaded.FindFund(cash));
        var bankOpening = loaded.CurrentPeriod!.InitialBalances.Where(b => b.FundId == bank).Sum(b => b.Amount.Amount);
        Assert.Equal(500m, bankOpening);
    }

    [Fact]
    public async Task Remove_fund_is_blocked_by_a_referencing_expense()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_fund_block");
        var account = await CreateAccount(client, "FundBlock");
        var (food, _) = await SeedAsync(client, account.Id, auth.UserId);

        var fundId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/funds", new CreateFundRequest("Wallet")));
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 10m, fundId, When))).EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/funds/{fundId}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --- Contribution (income) categories ------------------------------------

    [Fact]
    public async Task Create_edit_and_remove_a_contribution_category()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cc");
        var account = await CreateAccount(client, "CC");
        await SeedAsync(client, account.Id, auth.UserId);

        var ccId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/contribution-categories",
            new CreateContributionCategoryRequest("Salary", Icon: "wage")));
        var created = (await LoadAsync(client, account.Id)).FindContributionCategory(ccId)!;
        Assert.Equal("Salary", created.Name);
        Assert.Equal("wage", created.Icon);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/contribution-categories/{ccId}",
            new EditContributionCategoryRequest("Wages", Icon: "cash"))).EnsureSuccessStatusCode();
        Assert.Equal("Wages", (await LoadAsync(client, account.Id)).FindContributionCategory(ccId)!.Name);

        (await client.DeleteAsync($"/accounts/{account.Id}/contribution-categories/{ccId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindContributionCategory(ccId));
    }

    [Fact]
    public async Task Remove_contribution_category_is_blocked_by_a_referencing_deposit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("st_cc_block");
        var account = await CreateAccount(client, "CCBlock");
        var (_, bank) = await SeedAsync(client, account.Id, auth.UserId);

        var ccId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/contribution-categories",
            new CreateContributionCategoryRequest("Salary")));
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/deposits",
            new AddDepositRequest(ccId, bank, 500m, When))).EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/contribution-categories/{ccId}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_create_a_category()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("st_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("st_intruder");
        var account = await CreateAccount(owner, "Private");
        await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/categories", new CreateCategoryRequest("Sneaky"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
