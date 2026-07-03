using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Tracks whether a user has confirmed ownership of their email address and issues one-time verification
/// tokens (sent as a link). Verified state lives in <c>EmailVerifications</c> (UserId → VerifiedAt); pending
/// tokens live in <c>EmailVerificationTokens</c> (only the SHA-256 hash is stored, 24h TTL, single use).
/// If the confirmed email later changes, verification is cleared. Same migration-free idempotent-table
/// pattern as <see cref="RefreshTokenService"/>, so it works on both SQLite and Postgres.
/// </summary>
public sealed class EmailVerificationService(FinAppDbContext db)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"EmailVerifications\" (" +
            "\"UserId\" text PRIMARY KEY, \"Email\" text NOT NULL, \"VerifiedAt\" text NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"EmailVerificationTokens\" (" +
            "\"TokenHash\" text PRIMARY KEY, \"UserId\" text NOT NULL, \"Email\" text NOT NULL, " +
            "\"ExpiresAt\" text NOT NULL, \"UsedAt\" text NULL)", ct);
    }

    /// <summary>Is the user's <paramref name="currentEmail"/> confirmed? False if never verified or the email changed since.</summary>
    public async Task<bool> IsVerifiedAsync(Guid userId, string currentEmail, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Email\" FROM \"EmailVerifications\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            var verifiedEmail = (await cmd.ExecuteScalarAsync(ct)) as string;
            return verifiedEmail is not null &&
                   string.Equals(verifiedEmail, currentEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Mark a user's email verified directly (e.g. external sign-in, where the provider already vouched for it).</summary>
    public Task MarkVerifiedAsync(Guid userId, string email, CancellationToken ct = default) =>
        SetVerifiedAsync(userId, email, ct);

    /// <summary>Mint a one-time verification token for the user's current email; returns the raw token for the link.</summary>
    public async Task<string> IssueTokenAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"EmailVerificationTokens\" (\"TokenHash\", \"UserId\", \"Email\", \"ExpiresAt\") " +
                "VALUES (@hash, @uid, @email, @expires)";
            AddParam(cmd, "@hash", Hash(token));
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@email", email.Trim());
            AddParam(cmd, "@expires", Iso(DateTimeOffset.UtcNow.Add(Ttl)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return token;
    }

    /// <summary>Redeem a verification token exactly once; on success marks the email verified. Returns true if verified.</summary>
    public async Task<bool> VerifyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = Hash(token);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            Guid userId; string email;
            await using (var load = conn.CreateCommand())
            {
                load.CommandText =
                    "SELECT \"UserId\", \"Email\", \"ExpiresAt\", \"UsedAt\" FROM \"EmailVerificationTokens\" WHERE \"TokenHash\" = @hash";
                AddParam(load, "@hash", hash);
                await using var reader = await load.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return false;
                if (!await reader.IsDBNullAsync(3, ct)) return false;  // already used
                var expiresAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (expiresAt <= DateTimeOffset.UtcNow) return false;
                userId = Guid.Parse(reader.GetString(0));
                email = reader.GetString(1);
            }

            await using (var claim = conn.CreateCommand())
            {
                claim.CommandText =
                    "UPDATE \"EmailVerificationTokens\" SET \"UsedAt\" = @at WHERE \"TokenHash\" = @hash AND \"UsedAt\" IS NULL";
                AddParam(claim, "@at", Iso(DateTimeOffset.UtcNow));
                AddParam(claim, "@hash", hash);
                if (await claim.ExecuteNonQueryAsync(ct) != 1) return false;  // lost a concurrent redeem
            }

            await SetVerifiedInternalAsync(conn, userId, email, ct);
            return true;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task SetVerifiedAsync(Guid userId, string email, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try { await SetVerifiedInternalAsync(conn, userId, email, ct); }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task SetVerifiedInternalAsync(System.Data.Common.DbConnection conn, Guid userId, string email, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO \"EmailVerifications\" (\"UserId\", \"Email\", \"VerifiedAt\") VALUES (@uid, @email, @at) " +
            "ON CONFLICT (\"UserId\") DO UPDATE SET \"Email\" = @email, \"VerifiedAt\" = @at";
        AddParam(cmd, "@uid", userId.ToString());
        AddParam(cmd, "@email", email.Trim());
        AddParam(cmd, "@at", Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
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
