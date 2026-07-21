using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Issues one-time password-reset tokens (sent as a link) and redeems them exactly once. Tokens live in
/// <c>PasswordResetTokens</c> — only the SHA-256 hash is stored, with a short (1h) TTL and single use.
/// Same migration-free idempotent-table pattern as <see cref="EmailVerificationService"/>, so it works on
/// both SQLite and Postgres. The token is opaque and carries no user id — redeeming it is what reveals the user.
/// </summary>
public sealed class PasswordResetService(FinAppDbContext db)
{
    // Short-lived on purpose: a reset link is a bearer credential to the account, so it shouldn't linger.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"PasswordResetTokens\" (" +
            "\"TokenHash\" text PRIMARY KEY, \"UserId\" text NOT NULL, " +
            "\"ExpiresAt\" text NOT NULL, \"UsedAt\" text NULL)", ct);

    /// <summary>Mint a one-time reset token for a user; returns the raw token for the link.</summary>
    public async Task<string> IssueTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"PasswordResetTokens\" (\"TokenHash\", \"UserId\", \"ExpiresAt\") VALUES (@hash, @uid, @expires)";
            AddParam(cmd, "@hash", Hash(token));
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@expires", Iso(DateTimeOffset.UtcNow.Add(Ttl)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return token;
    }

    /// <summary>Redeem a reset token exactly once. Returns the user id it belonged to, or null when the token is
    /// unknown, expired or already used. A successful call marks it used, so it can't reset a password twice.</summary>
    public async Task<Guid?> ConsumeAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            Guid userId;
            await using (var load = conn.CreateCommand())
            {
                load.CommandText =
                    "SELECT \"UserId\", \"ExpiresAt\", \"UsedAt\" FROM \"PasswordResetTokens\" WHERE \"TokenHash\" = @hash";
                AddParam(load, "@hash", hash);
                await using var reader = await load.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                if (!await reader.IsDBNullAsync(2, ct)) return null;  // already used
                var expiresAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (expiresAt <= DateTimeOffset.UtcNow) return null;
                userId = Guid.Parse(reader.GetString(0));
            }

            await using (var claim = conn.CreateCommand())
            {
                claim.CommandText =
                    "UPDATE \"PasswordResetTokens\" SET \"UsedAt\" = @at WHERE \"TokenHash\" = @hash AND \"UsedAt\" IS NULL";
                AddParam(claim, "@at", Iso(DateTimeOffset.UtcNow));
                AddParam(claim, "@hash", hash);
                if (await claim.ExecuteNonQueryAsync(ct) != 1) return null;  // lost a concurrent redeem
            }
            return userId;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

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
