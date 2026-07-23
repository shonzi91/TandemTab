using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Tests;

/// <summary>
/// Recurring items — CRUD + pause/resume + the due-item handlers (confirm posts a real transaction and tunes a
/// "typical" estimate; skip marks handled without posting). Mirrors the client's recurring methods; confirm/skip
/// go through the shared <c>Period.PostRecurring</c>. CRUD verified via snapshot; confirm/skip via /overview.
/// </summary>
public class RecurringApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RecurringApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: funds + a "Rent" spend category + a "Salary" contribution category + an open Jan-2026 period
    /// (no deposit). Returns (rent, salary, bankFund).</summary>
    private static async Task<(Guid Rent, Guid Salary, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rent = agg.AddCategory("Rent").Id;
        var salary = agg.AddContributionCategory("Salary").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (rent, salary, fund);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task Create_recurring_expense_stores_its_fields()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_create");
        var account = await CreateAccount(client, "Data");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund, Icon: "home", AutoPost: true)));

        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal("Rent", item.Name);
        Assert.Equal(RecurringKind.Expense, item.Kind);
        Assert.Equal(RecurringAmountMode.Fixed, item.AmountMode);
        Assert.Equal(500m, item.ExpectedAmount);
        Assert.Equal(15, item.DayOfMonth);
        Assert.Equal(rent, item.CategoryId);
        Assert.True(item.AutoPost);
        Assert.NotNull(item.CreatedOn);
    }

    [Fact]
    public async Task Update_then_pause_a_recurring_item()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_update");
        var account = await CreateAccount(client, "Upd");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}",
            new UpdateRecurringRequest("Rent+", "typical", 550m, 20, rent, fund))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/active", new SetActiveRequest(false))).EnsureSuccessStatusCode();

        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal("Rent+", item.Name);
        Assert.Equal(RecurringAmountMode.Typical, item.AmountMode);
        Assert.Equal(550m, item.ExpectedAmount);
        Assert.Equal(20, item.DayOfMonth);
        Assert.False(item.Active);
    }

    [Fact]
    public async Task Delete_a_recurring_item()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_del");
        var account = await CreateAccount(client, "Del");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.DeleteAsync($"/accounts/{account.Id}/recurring/{recId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindRecurring(recId));
    }

    [Fact]
    public async Task Confirm_posts_an_expense_and_tunes_a_typical_estimate()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_confirm");
        var account = await CreateAccount(client, "Confirm");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Utilities", "expense", "typical", 500m, 15, rent, fund)));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(520m))).EnsureSuccessStatusCode();

        Assert.Equal(520m, (await Overview(client, account.Id))!.Spent);   // the real amount posted
        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal(510m, item.ExpectedAmount);                          // typical nudged halfway: (500+520)/2
        Assert.Equal(new DateOnly(2026, 1, 1), item.LastHandledPeriodFrom); // marked handled for this period
    }

    [Fact]
    public async Task Confirm_income_posts_a_deposit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_income");
        var account = await CreateAccount(client, "Income");
        var (_, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Salary", "income", "fixed", 2000m, 1, salary, fund)));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(2000m))).EnsureSuccessStatusCode();

        Assert.Equal(2000m, (await Overview(client, account.Id))!.Contributed);
    }

    [Fact]
    public async Task Skip_marks_handled_without_posting()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_skip");
        var account = await CreateAccount(client, "Skip");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PostAsync($"/accounts/{account.Id}/recurring/{recId}/skip", content: null)).EnsureSuccessStatusCode();

        Assert.Equal(0m, (await Overview(client, account.Id))!.Spent);   // nothing posted
        Assert.Equal(new DateOnly(2026, 1, 1), (await LoadAsync(client, account.Id)).FindRecurring(recId)!.LastHandledPeriodFrom);
    }

    [Fact]
    public async Task Create_with_an_unknown_category_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_badcat");
        var account = await CreateAccount(client, "BadCat");
        var (_, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, Guid.NewGuid(), fund));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_with_an_unknown_kind_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_badkind");
        var account = await CreateAccount(client, "BadKind");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "nonsense", "fixed", 500m, 15, rent, fund));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stranger_cannot_create_a_recurring_item()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("rc_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("rc_intruder");
        var account = await CreateAccount(owner, "Private");
        var (rent, _, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
