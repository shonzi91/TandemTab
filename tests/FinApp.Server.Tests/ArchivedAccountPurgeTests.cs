using System.Globalization;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// The startup sweep that hard-deletes accounts past their 30-day archive grace. It runs on every instance
/// before the app is listening, so the tests that matter are the failure ones: a purge that throws is a
/// container that never serves (this actually happened in production on a multi-instance deploy).
/// </summary>
public class ArchivedAccountPurgeTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public ArchivedAccountPurgeTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<Guid> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!.Id;

    /// <summary>Archive an account and backdate the row, so the sweep sees an expired grace window.</summary>
    private async Task ArchiveAsync(Guid accountId, int daysAgo)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>().ArchiveAsync(accountId);
        var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
        var at = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToString("O", CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"ArchivedAccounts\" SET \"ArchivedAt\" = {0} WHERE \"AccountId\" = {1}", at, accountId.ToString());
    }

    private async Task<int> PurgeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>().PurgeExpiredAsync();
    }

    private async Task<(bool Account, bool ArchiveRow)> ExistsAsync(Guid accountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
        var account = await db.Accounts.AnyAsync(a => a.Id == accountId);
        var archived = (await scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>().ArchivedIdsAsync())
            .Contains(accountId);
        return (account, archived);
    }

    [Fact]
    public async Task An_account_past_its_grace_window_is_deleted_and_its_archive_row_with_it()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("purge-expired");
        var accountId = await CreateAccount(client, "Old");
        await ArchiveAsync(accountId, ArchivedAccountsService.RetentionDays + 1);

        Assert.Equal(1, await PurgeAsync());

        var (account, archiveRow) = await ExistsAsync(accountId);
        Assert.False(account);
        Assert.False(archiveRow);
    }

    [Fact]
    public async Task An_account_still_inside_its_grace_window_is_left_alone()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("purge-fresh");
        var accountId = await CreateAccount(client, "Recent");
        await ArchiveAsync(accountId, ArchivedAccountsService.RetentionDays - 1);

        Assert.Equal(0, await PurgeAsync());

        var (account, archiveRow) = await ExistsAsync(accountId);
        Assert.True(account);
        Assert.True(archiveRow);
    }

    /// <summary>
    /// The state a crashed or raced purge leaves behind: the account is gone but its archive row survived.
    /// The sweep has to clean that up silently — this is the path that used to abort the process at startup.
    /// </summary>
    [Fact]
    public async Task A_stale_archive_row_whose_account_is_already_gone_is_cleaned_without_throwing()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("purge-stale");
        var accountId = await CreateAccount(client, "Half purged");
        await ArchiveAsync(accountId, ArchivedAccountsService.RetentionDays + 5);

        // Delete the account out of band, exactly as another instance winning the race would.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
            db.Accounts.Remove(await db.Accounts.SingleAsync(a => a.Id == accountId));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await PurgeAsync());
        Assert.False((await ExistsAsync(accountId)).ArchiveRow);
    }

    /// <summary>One wedged account must not take the rest of the sweep — or the process — down with it.</summary>
    [Fact]
    public async Task A_failing_account_does_not_stop_the_others_being_purged()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("purge-batch");
        var first = await CreateAccount(client, "One");
        var second = await CreateAccount(client, "Two");
        await ArchiveAsync(first, ArchivedAccountsService.RetentionDays + 2);
        await ArchiveAsync(second, ArchivedAccountsService.RetentionDays + 2);
        // An archive row pointing at an account that never existed: the loop must shrug and carry on.
        await ArchiveAsync(Guid.NewGuid(), ArchivedAccountsService.RetentionDays + 2);

        Assert.Equal(3, await PurgeAsync());
        Assert.False((await ExistsAsync(first)).Account);
        Assert.False((await ExistsAsync(second)).Account);
    }
}
