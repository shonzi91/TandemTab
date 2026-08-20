using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// Tag endpoints — create/edit/archive/remove of the flat cross-cutting labels, plus attaching them to an expense
/// through the add/edit-expense endpoints. Verified by deserializing the account snapshot.
/// </summary>
public class TagApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;
    public TagApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

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

    [Fact]
    public async Task Create_edit_archive_remove_tag()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tag_crud");
        var account = await CreateAccount(client, "Tags");
        await SeedAsync(client, account.Id, auth.UserId);

        var tagId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Vacation", "🏖️")));
        var tag = (await LoadAsync(client, account.Id)).FindTag(tagId)!;
        Assert.Equal("Vacation", tag.Name);
        Assert.Equal("🏖️", tag.Icon);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{tagId}", new EditTagRequest("Trip"))).EnsureSuccessStatusCode();
        Assert.Equal("Trip", (await LoadAsync(client, account.Id)).FindTag(tagId)!.Name);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{tagId}/archived", new SetArchivedRequest(true))).EnsureSuccessStatusCode();
        Assert.True((await LoadAsync(client, account.Id)).FindTag(tagId)!.IsArchived);

        (await client.DeleteAsync($"/accounts/{account.Id}/tags/{tagId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindTag(tagId));
    }

    [Fact]
    public async Task Duplicate_tag_name_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tag_dup");
        var account = await CreateAccount(client, "Tags");
        await SeedAsync(client, account.Id, auth.UserId);
        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Work")));

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("  work "));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Adding_an_expense_with_a_tag_attaches_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tag_expense");
        var account = await CreateAccount(client, "Tags");
        var (food, bank) = await SeedAsync(client, account.Id, auth.UserId);
        var trip = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Trip")));

        var addResp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 20m, bank, When, TagId: trip));
        addResp.EnsureSuccessStatusCode();
        var expenseId = (await addResp.Content.ReadFromJsonAsync<ExpenseMutationDto>())!.EntityId;

        var expense = (await LoadAsync(client, account.Id)).Periods[0].Expenses.Single();
        Assert.Equal(trip, expense.TagId);

        // Editing with a different tag replaces it. (A bogus id is ignored rather than obeyed — and since S111 that
        // means the stored tag stays, instead of the row being stripped by a client that got the id wrong.)
        var work = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Work")));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}",
            new EditExpenseRequest(food, 22m, bank, When, TagId: work))).EnsureSuccessStatusCode();

        var edited = (await LoadAsync(client, account.Id)).Periods[0].Expenses.Single();
        Assert.Equal(work, edited.TagId);
    }

    /// <summary>
    /// ★ The trap this closes: the same request treated an omitted <c>TagId</c> and an omitted <c>Time</c> by
    /// opposite rules — the tag cleared, the time was left alone — and the reasoning written next to the time
    /// applies word for word to the tag. It cost a real bug: the native edit omitted the tag, so correcting an
    /// amount on the phone silently stripped the label. Both fields now mean "leave it alone" when omitted, and
    /// clearing either is explicit.
    /// </summary>
    [Fact]
    public async Task An_omitted_tag_on_an_edit_keeps_the_label_and_clearing_it_is_explicit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tag_edit_omit");
        var account = await CreateAccount(client, "Tags");
        var (food, bank) = await SeedAsync(client, account.Id, auth.UserId);
        var label = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/tags", new CreateTagRequest("Split")));

        var addResp = await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
            new AddExpenseRequest(food, 20m, bank, When, TagId: label));
        var expenseId = (await addResp.Content.ReadFromJsonAsync<ExpenseMutationDto>())!.EntityId;

        // Correct the amount and say nothing about the label — exactly what an older client does.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{expenseId}",
            new EditExpenseRequest(food, 25m, bank, When))).EnsureSuccessStatusCode();

        var kept = (await LoadAsync(client, account.Id)).Periods[0].Expenses.Single();
        Assert.Equal(label, kept.TagId);
        Assert.Equal(25m, kept.Amount.Amount);

        // Saying so out loud still clears it.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/expenses/{kept.Id}",
            new EditExpenseRequest(food, 25m, bank, When, ClearTag: true))).EnsureSuccessStatusCode();

        Assert.Null((await LoadAsync(client, account.Id)).Periods[0].Expenses.Single().TagId);
    }
}
