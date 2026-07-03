using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Issues short-lived, single-use <b>authorization codes</b> for the external (Google/Facebook) sign-in flow.
///
/// After the provider callback authenticates the user, the server redirects the SPA to <c>/?authCode=…</c>
/// instead of putting a bearer token in the URL fragment. The code is an opaque handle that the SPA
/// immediately exchanges (over POST) for a real access + refresh token. This keeps the actual session tokens
/// out of the URL — where they'd otherwise leak into browser history, the Referer header, and server logs.
///
/// Only a SHA-256 hash of the code is stored; codes expire in two minutes and can be redeemed exactly once
/// (an atomic conditional update). Same migration-free idempotent-table pattern as <see cref="RefreshTokenService"/>.
/// </summary>
public sealed class AuthCodeService(FinAppDbContext db)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"AuthCodes\" (" +
            "\"CodeHash\" text PRIMARY KEY, \"UserId\" text NOT NULL, " +
            "\"ExpiresAt\" text NOT NULL, \"UsedAt\" text NULL)", ct);

    /// <summary>Mint a one-time code for a just-authenticated user; returns the raw code (shown once, in the redirect).</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"AuthCodes\" (\"CodeHash\", \"UserId\", \"ExpiresAt\") VALUES (@hash, @uid, @expires)";
            AddParam(cmd, "@hash", Hash(code));
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@expires", Iso(DateTimeOffset.UtcNow.Add(Ttl)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return code;
    }

    /// <summary>Redeem a code exactly once; returns the owning user id, or null if it's unknown, expired, or already used.</summary>
    public async Task<Guid?> RedeemAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var hash = Hash(code);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            // Atomically claim the code: only succeeds if it exists, is unused, and hasn't expired.
            Guid userId;
            await using (var load = conn.CreateCommand())
            {
                load.CommandText =
                    "SELECT \"UserId\", \"ExpiresAt\", \"UsedAt\" FROM \"AuthCodes\" WHERE \"CodeHash\" = @hash";
                AddParam(load, "@hash", hash);
                await using var reader = await load.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                if (!await reader.IsDBNullAsync(2, ct)) return null;   // already used
                var expiresAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (expiresAt <= DateTimeOffset.UtcNow) return null;
                userId = Guid.Parse(reader.GetString(0));
            }

            await using var claim = conn.CreateCommand();
            claim.CommandText =
                "UPDATE \"AuthCodes\" SET \"UsedAt\" = @at WHERE \"CodeHash\" = @hash AND \"UsedAt\" IS NULL";
            AddParam(claim, "@at", Iso(DateTimeOffset.UtcNow));
            AddParam(claim, "@hash", hash);
            var claimed = await claim.ExecuteNonQueryAsync(ct);
            return claimed == 1 ? userId : null;   // 0 rows => a concurrent redeem won the race
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Resolve a code to its user <b>without consuming it</b> (returns null if unknown/expired/used).
    /// Used by the 2FA login step so a mistyped code doesn't burn the ticket; redeem separately on success.</summary>
    public async Task<Guid?> ResolveAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT \"UserId\", \"ExpiresAt\", \"UsedAt\" FROM \"AuthCodes\" WHERE \"CodeHash\" = @hash";
            AddParam(cmd, "@hash", Hash(code));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            if (!await reader.IsDBNullAsync(2, ct)) return null;  // used
            var expiresAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (expiresAt <= DateTimeOffset.UtcNow) return null;
            return Guid.Parse(reader.GetString(0));
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static string Hash(string code) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<bool> OpenAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State == ConnectionState.Open) return false;
        await conn.OpenAsync(ct);
        return true;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
