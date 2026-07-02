using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinApp.Server.Auth;

/// <summary>
/// Issues and validates long-lived <b>refresh tokens</b> with rotation and reuse detection.
///
/// A refresh token is an opaque string <c>{id}.{secret}</c>. Only a SHA-256 hash of the secret is stored,
/// so a database leak can't be replayed. Each successful use <b>rotates</b> the token: the presented one is
/// revoked and a fresh one is issued, linked via <c>ReplacedById</c>. If an already-revoked token is ever
/// presented again (a sign a copy was stolen and replayed), we treat it as a breach and revoke <i>every</i>
/// active token for that user, forcing a re-login everywhere.
///
/// Backed by a standalone table created idempotently — the same migration-free pattern as
/// <see cref="ConsentService"/>, so it works on both the SQLite (dev/desktop) and Postgres (cloud) providers.
/// </summary>
public sealed class RefreshTokenService(FinAppDbContext db, IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"RefreshTokens\" (" +
            "\"Id\" text PRIMARY KEY, \"UserId\" text NOT NULL, \"TokenHash\" text NOT NULL, " +
            "\"CreatedAt\" text NOT NULL, \"ExpiresAt\" text NOT NULL, " +
            "\"RevokedAt\" text NULL, \"ReplacedById\" text NULL)", ct);

    /// <summary>Mint a fresh refresh token for a user and return the raw <c>{id}.{secret}</c> string (shown once).</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"RefreshTokens\" (\"Id\", \"UserId\", \"TokenHash\", \"CreatedAt\", \"ExpiresAt\") " +
                "VALUES (@id, @uid, @hash, @created, @expires)";
            AddParam(cmd, "@id", id.ToString());
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@hash", HashSecret(secret));
            AddParam(cmd, "@created", Iso(now));
            AddParam(cmd, "@expires", Iso(now.AddDays(_options.RefreshTokenDays)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }

        return $"{id:N}.{secret}";
    }

    /// <summary>
    /// Validate a refresh token and, on success, rotate it: returns the owning user id plus a brand-new
    /// refresh token, and revokes the presented one. Returns null if the token is malformed, unknown, expired,
    /// or already revoked. Presenting an already-revoked token additionally revokes all of that user's tokens.
    /// </summary>
    public async Task<(Guid UserId, string NewToken)?> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
    {
        if (!TryParse(rawToken, out var id, out var secret)) return null;

        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            var row = await LoadAsync(conn, id, ct);
            if (row is null) return null;
            var (userId, hash, expiresAt, revoked) = row.Value;

            // A revoked token being presented means someone replayed a stolen copy — nuke the whole family.
            if (revoked)
            {
                await RevokeAllForUserCoreAsync(conn, userId, ct);
                return null;
            }
            if (expiresAt <= DateTimeOffset.UtcNow) return null;
            if (!FixedTimeEquals(hash, HashSecret(secret))) return null;

            // Rotate: issue a successor, then revoke the presented token and link the two.
            var newId = Guid.NewGuid();
            var newSecret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = DateTimeOffset.UtcNow;

            await using (var insert = conn.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO \"RefreshTokens\" (\"Id\", \"UserId\", \"TokenHash\", \"CreatedAt\", \"ExpiresAt\") " +
                    "VALUES (@id, @uid, @hash, @created, @expires)";
                AddParam(insert, "@id", newId.ToString());
                AddParam(insert, "@uid", userId.ToString());
                AddParam(insert, "@hash", HashSecret(newSecret));
                AddParam(insert, "@created", Iso(now));
                AddParam(insert, "@expires", Iso(now.AddDays(_options.RefreshTokenDays)));
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using (var revoke = conn.CreateCommand())
            {
                revoke.CommandText =
                    "UPDATE \"RefreshTokens\" SET \"RevokedAt\" = @at, \"ReplacedById\" = @next WHERE \"Id\" = @id";
                AddParam(revoke, "@at", Iso(now));
                AddParam(revoke, "@next", newId.ToString());
                AddParam(revoke, "@id", id.ToString());
                await revoke.ExecuteNonQueryAsync(ct);
            }

            return (userId, $"{newId:N}.{newSecret}");
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Revoke a refresh token on sign-out. No-op if it's unknown or already revoked.</summary>
    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        if (!TryParse(rawToken, out var id, out _)) return;
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE \"RefreshTokens\" SET \"RevokedAt\" = @at WHERE \"Id\" = @id AND \"RevokedAt\" IS NULL";
            AddParam(cmd, "@at", Iso(DateTimeOffset.UtcNow));
            AddParam(cmd, "@id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task RevokeAllForUserCoreAsync(System.Data.Common.DbConnection conn, Guid userId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE \"RefreshTokens\" SET \"RevokedAt\" = @at WHERE \"UserId\" = @uid AND \"RevokedAt\" IS NULL";
        AddParam(cmd, "@at", Iso(DateTimeOffset.UtcNow));
        AddParam(cmd, "@uid", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(Guid UserId, string Hash, DateTimeOffset ExpiresAt, bool Revoked)?> LoadAsync(
        System.Data.Common.DbConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT \"UserId\", \"TokenHash\", \"ExpiresAt\", \"RevokedAt\" FROM \"RefreshTokens\" WHERE \"Id\" = @id";
        AddParam(cmd, "@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                !await reader.IsDBNullAsync(3, ct));
    }

    private static bool TryParse(string raw, out Guid id, out string secret)
    {
        id = Guid.Empty; secret = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var dot = raw.IndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1) return false;
        if (!Guid.TryParse(raw[..dot], out id)) return false;
        secret = raw[(dot + 1)..];
        return true;
    }

    private static string HashSecret(string secret) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

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
