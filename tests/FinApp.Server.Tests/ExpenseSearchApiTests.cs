using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Searching every period's expenses — the read a thin client's "find an older expense" pickers need (S119).
///
/// <para>★ It exists because every other spending read is period-scoped, and the charge money comes back on is
/// routinely months old. The tests that matter are the ones about <b>reach</b>: an older row must be findable by
/// typing, not only by the cap happening to include it.</para>
/// </summary>
public class ExpenseSearchApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ExpenseSearchApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>June holds a €60 "Group dinner"; August holds a €12 "Coffee". Two periods, so the search has
    /// something to reach past.</summary>
    private static async Task SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var food = agg.AddCategory("Food").Id;
        var travel = agg.AddCategory("Travel").Id;
        var bank = agg.FundId("Bank");

        var june = agg.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        june.AddExpense(new Expense(food, new Money(60m, "EUR"), new DateOnly(2026, 6, 12), memberId, bank, "Group dinner"));
        june.AddExpense(new Expense(travel, new Money(46.80m, "EUR"), new DateOnly(2026, 6, 14), memberId, bank, "Tickets"));
        var august = agg.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        august.AddExpense(new Expense(food, new Money(12m, "EUR"), new DateOnly(2026, 8, 3), memberId, bank, "Coffee"));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
    }

    private static Task<ExpenseSearchDto?> Search(HttpClient client, Guid accountId, string query = "") =>
        client.GetFromJsonAsync<ExpenseSearchDto>($"/accounts/{accountId}/expenses/search{query}");

    [Fact]
    public async Task It_returns_expenses_from_every_period_newest_first()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_all");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        var found = (await Search(client, account.Id))!;

        Assert.Equal("EUR", found.Currency);
        Assert.Equal(3, found.TotalCount);
        Assert.Equal(3, found.Rows.Count);
        Assert.Equal("Coffee", found.Rows[0].Note);         // August, newest
        Assert.Equal("Group dinner", found.Rows[2].Note);   // June, oldest
        // The display fields are resolved server-side, so the picker needs no lookups of its own.
        Assert.Equal("Food", found.Rows[0].CategoryName);
    }

    [Fact]
    public async Task A_note_reaches_a_charge_two_periods_back()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_note");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        var found = (await Search(client, account.Id, "?q=dinner"))!;

        // ★ The whole point: the row a refund belongs to is reachable by typing what you remember about it,
        // months after the period it lives in stopped being the one on screen.
        Assert.Equal("Group dinner", Assert.Single(found.Rows).Note);
    }

    [Fact]
    public async Task A_half_typed_amount_still_finds_the_row()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_amount");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        // "46.8" while the user is still typing, and "46,8" because half of Europe types the comma.
        Assert.Equal("Tickets", Assert.Single((await Search(client, account.Id, "?q=46.8"))!.Rows).Note);
        Assert.Equal("Tickets", Assert.Single((await Search(client, account.Id, "?q=46,8"))!.Rows).Note);
    }

    [Fact]
    public async Task The_category_name_is_searchable_too()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_cat");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        var found = (await Search(client, account.Id, "?q=travel"))!;

        Assert.Equal("Tickets", Assert.Single(found.Rows).Note);
    }

    [Fact]
    public async Task RefundableOnly_drops_a_row_with_nothing_left_on_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_refundable");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        var coffee = (await Search(client, account.Id, "?q=Coffee"))!.Rows.Single().Id;
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses/{coffee}/refund",
            new RefundExpenseRequest(12m))).EnsureSuccessStatusCode();

        // You cannot get money back off an expense that has already been refunded to nothing.
        Assert.Empty((await Search(client, account.Id, "?q=Coffee&refundableOnly=true"))!.Rows);
        // ...but it is still an expense, so the unfiltered search keeps reporting it.
        Assert.Single((await Search(client, account.Id, "?q=Coffee"))!.Rows);
    }

    [Fact]
    public async Task The_total_says_what_a_capped_list_is_not_showing()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("xs_cap");
        var account = await CreateAccount(client, "Personal");
        await SeedAsync(client, account.Id, auth.UserId);

        var found = (await Search(client, account.Id, "?take=1"))!;

        // A cap that looks like the end of the history is how a picker convinces someone their row is gone.
        Assert.Single(found.Rows);
        Assert.Equal(3, found.TotalCount);
    }

    [Fact]
    public async Task Stranger_cannot_search_someone_elses_expenses()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("xs_owner");
        var account = await CreateAccount(owner, "Private");
        await SeedAsync(owner, account.Id, auth.UserId);

        var (stranger, _) = await _factory.RegisterAndAuthAsync("xs_stranger");
        var resp = await stranger.GetAsync($"/accounts/{account.Id}/expenses/search");

        Assert.False(resp.IsSuccessStatusCode);
    }
}
