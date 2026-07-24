using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;

namespace FinApp.Server.Tests;

/// <summary>
/// Statement import — commit a batch of reviewed rows in one save: a negative amount → an expense, a positive one →
/// income; zero/empty rows are skipped; a missing category/fund fails the whole batch. Mirrors
/// <c>BudgetingState.ImportTransactions</c>. Verified via the snapshot round-trip.
/// </summary>
public class StatementImportApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public StatementImportApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: default funds + a "Groceries" spend category + a "Salary" contribution category + an open
    /// Jan-2026 period. Returns (groceries, salary, bank fund).</summary>
    private static async Task<(Guid Groceries, Guid Salary, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var groceries = agg.AddCategory("Groceries").Id;
        var salary = agg.AddContributionCategory("Salary").Id;
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (groceries, salary, agg.FundId("Bank"));
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    [Fact]
    public async Task Import_posts_negatives_as_expenses_and_positives_as_income()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_mixed");
        var account = await CreateAccount(client, "Mixed");
        var (groceries, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var res = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[]
            {
                new ImportRowDto(-42.50m, new DateOnly(2026, 1, 8), groceries, fund, "Tesco"),
                new ImportRowDto(-10m, new DateOnly(2026, 1, 9), groceries, fund, null),
                new ImportRowDto(2000m, new DateOnly(2026, 1, 1), salary, fund, "Payday"),
            }))).Content.ReadFromJsonAsync<ImportResultDto>();

        Assert.Equal(3, res!.Imported);
        Assert.Equal(0, res.Skipped);

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        Assert.Equal(2, period.Expenses.Count);
        Assert.Contains(period.Expenses, e => e.Amount.Amount == 42.50m && e.CategoryId == groceries && e.Note == "Tesco");
        Assert.Equal(2000m, period.Contributions.Where(c => c.MemberId == auth.UserId).Sum(c => c.Paid.Amount));
    }

    [Fact]
    public async Task Import_skips_zero_and_empty_rows_and_reports_the_count()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_skip");
        var account = await CreateAccount(client, "Skip");
        var (groceries, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var res = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[]
            {
                new ImportRowDto(-20m, new DateOnly(2026, 1, 8), groceries, fund, null),   // imported
                new ImportRowDto(0m, new DateOnly(2026, 1, 8), groceries, fund, null),      // zero → skip
                new ImportRowDto(-5m, new DateOnly(2026, 1, 8), Guid.Empty, fund, null),    // no category → skip
                new ImportRowDto(-5m, new DateOnly(2026, 1, 8), groceries, Guid.Empty, null), // no fund → skip
            }))).Content.ReadFromJsonAsync<ImportResultDto>();

        Assert.Equal(1, res!.Imported);
        Assert.Equal(3, res.Skipped);
        Assert.Single((await LoadAsync(client, account.Id)).CurrentPeriod!.Expenses);
    }

    [Fact]
    public async Task A_row_with_an_unknown_fund_fails_the_whole_batch()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_badfund");
        var account = await CreateAccount(client, "BadFund");
        var (groceries, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[]
            {
                new ImportRowDto(-20m, new DateOnly(2026, 1, 8), groceries, fund, null),
                new ImportRowDto(-30m, new DateOnly(2026, 1, 9), groceries, Guid.NewGuid(), null),   // bad fund
            }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty((await LoadAsync(client, account.Id)).CurrentPeriod!.Expenses);   // all-or-nothing: nothing posted
    }

    [Fact]
    public async Task A_negative_row_naming_a_non_spend_category_fails()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_badcat");
        var account = await CreateAccount(client, "BadCat");
        var (_, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // A negative amount is read as an expense, so a contribution category id isn't a valid spend category.
        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[] { new ImportRowDto(-20m, new DateOnly(2026, 1, 8), salary, fund, null) }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_positive_row_naming_a_non_income_category_fails()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_badinc");
        var account = await CreateAccount(client, "BadInc");
        var (groceries, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // A positive amount is read as income, so a spend category id isn't a valid contribution category.
        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[] { new ImportRowDto(2000m, new DateOnly(2026, 1, 1), groceries, fund, null) }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_empty_batch_imports_nothing()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_empty");
        var account = await CreateAccount(client, "Empty");
        await SeedAsync(client, account.Id, auth.UserId);

        var res = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()))).Content.ReadFromJsonAsync<ImportResultDto>();
        Assert.Equal(0, res!.Imported);
        Assert.Equal(0, res.Skipped);
    }

    [Fact]
    public async Task Stranger_cannot_import()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("im_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("im_intruder");
        var account = await CreateAccount(owner, "Private");
        var (groceries, _, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[] { new ImportRowDto(-20m, new DateOnly(2026, 1, 8), groceries, fund, null) }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Re_importing_the_same_statement_skips_duplicates_but_keeps_in_batch_repeats()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("im_dupe");
        var account = await CreateAccount(client, "Dupe");
        var (groceries, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        // A fresh batch with TWO identical rows: both post (they only dedupe against PRE-EXISTING data, not each other).
        var first = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[]
            {
                new ImportRowDto(-42.50m, new DateOnly(2026, 1, 8), groceries, fund, "Tesco"),
                new ImportRowDto(-42.50m, new DateOnly(2026, 1, 8), groceries, fund, "Tesco"),
                new ImportRowDto(2000m, new DateOnly(2026, 1, 1), salary, fund, "Payday"),
            }))).Content.ReadFromJsonAsync<ImportResultDto>();
        Assert.Equal(3, first!.Imported);
        Assert.Equal(0, first.Duplicates);

        // Re-import the same statement (default SkipDuplicates=true): every row now matches existing data → all skipped.
        var again = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[]
            {
                new ImportRowDto(-42.50m, new DateOnly(2026, 1, 8), groceries, fund, "Tesco"),
                new ImportRowDto(2000m, new DateOnly(2026, 1, 1), salary, fund, "Payday"),
            }))).Content.ReadFromJsonAsync<ImportResultDto>();
        Assert.Equal(0, again!.Imported);
        Assert.Equal(2, again.Duplicates);

        // Opting out re-imports them.
        var forced = await (await client.PostAsJsonAsync($"/accounts/{account.Id}/import",
            new ImportTransactionsRequest(new[] { new ImportRowDto(-42.50m, new DateOnly(2026, 1, 8), groceries, fund, "Tesco") }, SkipDuplicates: false)))
            .Content.ReadFromJsonAsync<ImportResultDto>();
        Assert.Equal(1, forced!.Imported);
        Assert.Equal(0, forced.Duplicates);
    }
}
