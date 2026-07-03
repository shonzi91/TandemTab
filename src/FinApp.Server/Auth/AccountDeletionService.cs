using System.Data;
using System.Globalization;
using FinApp.Domain.Accounts;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Handles a user deleting their whole account (identity), with a 30-day grace period.
///
/// <para>Requesting deletion is a <b>soft delete</b>: a row in the standalone <c>PendingUserDeletions</c>
/// (UserId → ScheduledAt) marks when the identity will be purged. During the window the user can log back in and
/// <see cref="CancelAsync"/> it (a "recycle bin"); their solo-owned accounts are archived alongside them and are
/// likewise restorable. Owning an account with <b>other members</b> blocks deletion until ownership is transferred,
/// so nobody else loses their data.</para>
///
/// <para><see cref="PurgeDueAsync"/> (run at startup, like the archived-account purge) hard-deletes identity data
/// past its date — GDPR erasure: the row can't linger recoverable forever. Entries the user made in <i>other</i>
/// people's shared accounts stay (they live in those accounts' opaque snapshots) — "keep the operations". Same
/// migration-free CREATE-TABLE-IF-NOT-EXISTS pattern as the other standalone tables.</para>
/// </summary>
public sealed class AccountDeletionService(FinAppDbContext db, TwoFactorService twoFactor, ArchivedAccountsService archives)
{
    public const int GraceDays = 30;

    // Every standalone table keyed by UserId — erased on final purge so no identity data is left behind.
    private static readonly (string Table, string Column)[] UserKeyedTables =
    [
        ("TwoFactor", "UserId"), ("TwoFactorRecoveryCodes", "UserId"),
        ("EmailVerifications", "UserId"), ("EmailVerificationTokens", "UserId"),
        ("RefreshTokens", "UserId"), ("ExternalIdentities", "UserId"),
        ("UserAvatars", "UserId"), ("Consents", "UserId"), ("AuthCodes", "UserId"),
    ];

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"PendingUserDeletions\" (" +
            "\"UserId\" text PRIMARY KEY, \"ScheduledAt\" text NOT NULL)", ct);

    /// <summary>When the user's identity is scheduled to be purged, or null if no deletion is pending.</summary>
    public async Task<DateTimeOffset?> ScheduledAtAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"ScheduledAt\" FROM \"PendingUserDeletions\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            return (await cmd.ExecuteScalarAsync(ct)) is string s
                ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Accounts the user owns that still have other members — deletion is blocked until these are transferred.</summary>
    public async Task<IReadOnlyList<Account>> OwnedSharedAccountsAsync(Guid userId, CancellationToken ct = default) =>
        await db.Accounts.Include(a => a.Members)
            .Where(a => a.OwnerUserId == userId && a.Members.Count > 1)
            .ToListAsync(ct);

    /// <summary>Schedule the user's deletion after verifying the second factor (when 2FA is on) and that they don't
    /// own any shared account. Archives their solo-owned accounts so those are recoverable during the grace too.</summary>
    public async Task RequestAsync(Guid userId, string? twoFactorCode, CancellationToken ct = default)
    {
        if (await twoFactor.IsEnabledAsync(userId, ct) && !await twoFactor.VerifyAsync(userId, twoFactorCode ?? "", ct))
            throw new UnauthorizedException("That two-factor code isn't right. Try again.");

        if ((await OwnedSharedAccountsAsync(userId, ct)).Count > 0)
            throw new BadRequestException("Transfer ownership of your shared accounts before deleting your account.");

        // Archive the user's solo-owned accounts (recoverable during the grace, then purged with the archive sweep).
        var soloOwned = await db.Accounts.Include(a => a.Members)
            .Where(a => a.OwnerUserId == userId && a.Members.Count <= 1)
            .Select(a => a.Id)
            .ToListAsync(ct);
        foreach (var accountId in soloOwned)
            await archives.ArchiveAsync(accountId, ct);

        await UpsertAsync(userId, DateTimeOffset.UtcNow.AddDays(GraceDays), ct);
    }

    /// <summary>Cancel a pending deletion (the user logged back in and chose to keep the account).</summary>
    public async Task CancelAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM \"PendingUserDeletions\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Hard-delete every user whose grace period has elapsed. Returns how many were purged. Best-effort per
    /// user so one failure doesn't stall the sweep. Called at startup alongside the archived-account purge.</summary>
    public async Task<int> PurgeDueAsync(CancellationToken ct = default)
    {
        var due = new List<Guid>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"UserId\" FROM \"PendingUserDeletions\" WHERE \"ScheduledAt\" <= @now";
            AddParam(cmd, "@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) due.Add(Guid.Parse(reader.GetString(0)));
        }
        finally { if (opened) await conn.CloseAsync(); }

        var purged = 0;
        foreach (var userId in due)
        {
            try { await PurgeAsync(userId, ct); purged++; }
            catch { /* leave it for the next sweep */ }
        }
        return purged;
    }

    /// <summary>Erase one user's identity: their solo-owned accounts, memberships, every user-keyed standalone table,
    /// and the User row. Entries they made in other people's accounts are untouched (opaque snapshots).</summary>
    public async Task PurgeAsync(Guid userId, CancellationToken ct = default)
    {
        // Owned accounts (already archived at request time) — remove them and their content.
        var owned = await db.Accounts.Where(a => a.OwnerUserId == userId).ToListAsync(ct);
        db.Accounts.RemoveRange(owned);
        // Drop the user's membership rows in everyone ELSE's accounts (their snapshot entries stay).
        var sharedWithUser = await db.Accounts.Include(a => a.Members)
            .Where(a => a.OwnerUserId != userId && a.Members.Any(m => m.UserId == userId))
            .ToListAsync(ct);
        foreach (var account in sharedWithUser)
            account.RemoveMember(userId);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not null) db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        // Every standalone user-keyed table + the pending-deletion marker itself.
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            foreach (var (table, column) in UserKeyedTables)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM \"{table}\" WHERE \"{column}\" = @uid";
                AddParam(cmd, "@uid", userId.ToString());
                try { await cmd.ExecuteNonQueryAsync(ct); } catch { /* table may not exist yet on a fresh db */ }
            }
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM \"PendingUserDeletions\" WHERE \"UserId\" = @uid";
            AddParam(del, "@uid", userId.ToString());
            await del.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task UpsertAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"PendingUserDeletions\" (\"UserId\", \"ScheduledAt\") VALUES (@uid, @at) " +
                "ON CONFLICT (\"UserId\") DO UPDATE SET \"ScheduledAt\" = @at";
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@at", at.ToString("O", CultureInfo.InvariantCulture));
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
