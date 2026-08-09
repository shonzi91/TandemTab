using System.Data;
using System.Globalization;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Accounts;

/// <summary>
/// Soft-deletion for accounts: when the last member leaves an account we archive it for a grace period
/// (<see cref="RetentionDays"/>) instead of deleting outright, so it can be reactivated. Backed by a
/// standalone <c>ArchivedAccounts</c> table created idempotently with <c>CREATE TABLE IF NOT EXISTS</c>
/// (same migration-free pattern as <see cref="FinApp.Server.Auth.AvatarService"/>: prod builds its schema via
/// <c>EnsureCreated</c>, which never ALTERs existing tables). The account row itself stays intact (members
/// included) so the leaver keeps access to restore it; it's just filtered out of the active account list.
/// </summary>
public sealed class ArchivedAccountsService(FinAppDbContext db, ILogger<ArchivedAccountsService> logger)
{
    public const int RetentionDays = 30;

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"ArchivedAccounts\" (\"AccountId\" text PRIMARY KEY, \"ArchivedAt\" text NOT NULL)", ct);

    public Task ArchiveAsync(Guid accountId, CancellationToken ct = default) =>
        WriteAsync(
            "INSERT INTO \"ArchivedAccounts\" (\"AccountId\", \"ArchivedAt\") VALUES (@id, @at) " +
            "ON CONFLICT (\"AccountId\") DO UPDATE SET \"ArchivedAt\" = @at",
            cmd => { AddParam(cmd, "@id", accountId.ToString()); AddParam(cmd, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); },
            ct);

    public Task UnarchiveAsync(Guid accountId, CancellationToken ct = default) =>
        WriteAsync("DELETE FROM \"ArchivedAccounts\" WHERE \"AccountId\" = @id",
            cmd => AddParam(cmd, "@id", accountId.ToString()), ct);

    /// <summary>The set of archived account ids (used to filter the active list).</summary>
    public async Task<HashSet<Guid>> ArchivedIdsAsync(CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"AccountId\" FROM \"ArchivedAccounts\"";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (Guid.TryParse(reader.GetString(0), out var id)) result.Add(id);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return result;
    }

    /// <summary>When each archived account was archived (for showing the remaining grace period).</summary>
    public async Task<Dictionary<Guid, DateTimeOffset>> ArchivedAtAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"AccountId\", \"ArchivedAt\" FROM \"ArchivedAccounts\"";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (Guid.TryParse(reader.GetString(0), out var id))
                    result[id] = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return result;
    }

    /// <summary>
    /// Hard-delete accounts whose grace period has elapsed. Safe to call at startup: never throws.
    /// </summary>
    /// <remarks>
    /// <para>Two things this has to survive, both of which have bitten production:</para>
    /// <para><b>A deploy starts several instances at once</b>, each running this sweep against the same rows.
    /// The loser of that race gets a <c>DbUpdateConcurrencyException</c> ("expected to affect 1 row(s), but
    /// actually affected 0") — which is just "somebody else already purged it". Every account is therefore its
    /// own save, and a failure is swallowed per-account and retried by the next sweep; the caller runs before
    /// the app is listening, so an exception here means the container never serves.</para>
    /// <para><b>The account row is deleted BEFORE the archive row.</b> <see cref="UnarchiveAsync"/> is raw SQL
    /// that commits immediately, so the other order leaves a half-done purge with an account that is neither
    /// archived nor deleted — it silently reappears in the user's list. This way the leftover is a stale
    /// archive row, which the next sweep cleans: the account query comes back null and only the unarchive runs.</para>
    /// </remarks>
    /// <returns>How many archived accounts were fully purged.</returns>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var expired = (await ArchivedAtAsync(ct)).Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        if (expired.Count == 0) return 0;

        var purged = 0;
        foreach (var id in expired)
        {
            try
            {
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (account is not null)
                {
                    db.Accounts.Remove(account);   // cascades to the account's owned rows
                    await db.SaveChangesAsync(ct);
                }
                await UnarchiveAsync(id, ct);
                purged++;
            }
            catch (Exception ex)
            {
                // Forget everything still staged, or the next account's save would retry this failed delete
                // and fail with it. Safe to clear wholesale: the purge runs in its own startup scope.
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Purge of archived account {AccountId} failed; leaving it for the next sweep.", id);
            }
        }
        return purged;
    }

    private async Task WriteAsync(string sql, Action<System.Data.Common.DbCommand> bind, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task<bool> OpenAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State == ConnectionState.Open) return false;
        await conn.OpenAsync(ct);
        return true;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
