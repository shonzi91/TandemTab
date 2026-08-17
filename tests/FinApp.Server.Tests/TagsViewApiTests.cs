using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The tag <b>manage</b> read — the half a thin client was missing. The only tag list it could fetch was the
/// Spending view's, which is built from <c>ActiveTags</c> because it is the picker source; a client working from it
/// could archive a tag and then never see it again. These pin the difference between the two reads, and the two
/// figures a manager needs that a picker never does: the archived flag and the use count behind the delete confirm.
/// </summary>
public class TagsViewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public TagsViewApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 6, 12);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Category, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        period.Deposit(memberId, new Money(3000m, "EUR"), fundId: fund);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund);
    }

    private static async Task<Guid> CreateTag(HttpClient client, Guid accountId, string name, bool tripTag = false)
    {
        var resp = await client.PostAsJsonAsync($"/accounts/{accountId}/tags", new CreateTagRequest(name, null, tripTag));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    private static Task<TagsViewDto?> Tags(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<TagsViewDto>($"/accounts/{accountId}/tags");

    [Fact]
    public async Task An_account_with_no_snapshot_reads_as_an_empty_list_not_a_404()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tagsview_empty");
        var account = await CreateAccount(client, "Tags");

        var view = (await Tags(client, account.Id))!;

        Assert.Empty(view.Tags);
        Assert.Empty(view.Categories);
    }

    /// <summary>
    /// The reason this endpoint exists. An archived tag is deliberately absent from the picker and must be present
    /// here — otherwise archiving is a one-way door and the restore action has nothing to act on.
    /// </summary>
    [Fact]
    public async Task An_archived_tag_is_absent_from_the_picker_and_present_in_the_manage_read()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tagsview_archived");
        var account = await CreateAccount(client, "Tags");
        await SeedAsync(client, account.Id, auth.UserId);
        var lidl = await CreateTag(client, account.Id, "Lidl");
        await CreateTag(client, account.Id, "Work");

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{lidl}/archived", new SetArchivedRequest(true)))
            .EnsureSuccessStatusCode();

        var picker = (await client.GetFromJsonAsync<SpendingViewDto>($"/accounts/{account.Id}/spending"))!;
        Assert.DoesNotContain(picker.TagOptions, t => t.Id == lidl);

        var manage = (await Tags(client, account.Id))!;
        var row = Assert.Single(manage.Tags, t => t.Id == lidl);
        Assert.True(row.Archived);
    }

    /// <summary>Live labels first, the archive at the bottom — an archived tag sorting between two live ones reads
    /// as a list that has lost its order rather than one with a section.</summary>
    [Fact]
    public async Task Active_tags_sort_ahead_of_archived_ones_then_by_name()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tagsview_order");
        var account = await CreateAccount(client, "Tags");
        await SeedAsync(client, account.Id, auth.UserId);
        // Created deliberately out of order, so a pass is the sort rather than insertion order.
        var zebra = await CreateTag(client, account.Id, "Zebra");
        var apple = await CreateTag(client, account.Id, "Apple");
        var middle = await CreateTag(client, account.Id, "Middle");

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{middle}/archived", new SetArchivedRequest(true)))
            .EnsureSuccessStatusCode();

        var view = (await Tags(client, account.Id))!;

        Assert.Equal(new[] { apple, zebra, middle }, view.Tags.Select(t => t.Id));
    }

    /// <summary>
    /// <c>RemoveTag</c> is a hard delete that leaves tagged expenses holding a dangling id, so the count is what
    /// makes the confirm dialog a real question rather than a formality.
    /// </summary>
    [Fact]
    public async Task The_use_count_is_how_many_expenses_carry_the_tag()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tagsview_uses");
        var account = await CreateAccount(client, "Tags");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var lidl = await CreateTag(client, account.Id, "Lidl");
        var unused = await CreateTag(client, account.Id, "Never used");

        foreach (var amount in new[] { 12m, 30m, 8m })
            (await client.PostAsJsonAsync($"/accounts/{account.Id}/expenses",
                new AddExpenseRequest(cat, amount, fund, When, "Shop", TagId: lidl))).EnsureSuccessStatusCode();

        var view = (await Tags(client, account.Id))!;

        Assert.Equal(3, view.Tags.Single(t => t.Id == lidl).Uses);
        Assert.Equal(0, view.Tags.Single(t => t.Id == unused).Uses);
    }

    /// <summary>The F2 binding travels resolved, so the row can say "→ Food" without the client having fetched a
    /// category list of its own — this surface is reachable without the Spending view ever being loaded.</summary>
    [Fact]
    public async Task The_category_binding_travels_as_a_name_and_the_categories_come_with_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tagsview_binding");
        var account = await CreateAccount(client, "Tags");
        var (cat, _) = await SeedAsync(client, account.Id, auth.UserId);
        var lidl = await CreateTag(client, account.Id, "Lidl");

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/tags/{lidl}", new EditTagRequest("Lidl", null, cat)))
            .EnsureSuccessStatusCode();

        var view = (await Tags(client, account.Id))!;
        var row = view.Tags.Single(t => t.Id == lidl);

        Assert.Equal(cat, row.CategoryId);
        Assert.Equal("Food", row.CategoryName);
        Assert.Contains(view.Categories, c => c.Id == cat);
    }

    /// <summary>A trip label is an ordinary tag wearing a display hint — the manage surface has to show it as one,
    /// or the six seeded labels look like they belong to nobody.</summary>
    [Fact]
    public async Task A_trip_label_is_listed_and_flagged_rather_than_hidden()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tagsview_triptag");
        var account = await CreateAccount(client, "Tags");
        await SeedAsync(client, account.Id, auth.UserId);
        var stay = await CreateTag(client, account.Id, "Stay", tripTag: true);
        var work = await CreateTag(client, account.Id, "Work");

        var view = (await Tags(client, account.Id))!;

        Assert.True(view.Tags.Single(t => t.Id == stay).TripTag);
        Assert.False(view.Tags.Single(t => t.Id == work).TripTag);
    }
}
