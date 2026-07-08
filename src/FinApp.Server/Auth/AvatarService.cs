using System.Data;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Stores and serves user profile pictures (small data-URL images). Kept in a standalone
/// <c>UserAvatars</c> table created idempotently with <c>CREATE TABLE IF NOT EXISTS</c> — which works on
/// both SQLite (dev/tests) and Postgres (prod, where the schema is built via <c>EnsureCreated</c>), so no
/// EF migration is needed and the existing <c>Users</c> table is left untouched. Accessed via raw ADO so
/// the same SQL runs on either provider. The user id is stored as text to sidestep Guid-encoding differences.
/// </summary>
public sealed class AvatarService(FinAppDbContext db)
{
    private const int MaxLength = 400_000; // ~300 KB image as base64; plenty for a 128px avatar

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"UserAvatars\" (\"UserId\" text PRIMARY KEY, \"DataUrl\" text NOT NULL)", ct);

    public async Task<string?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"DataUrl\" FROM \"UserAvatars\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            return (await cmd.ExecuteScalarAsync(ct)) as string;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Avatars for a set of users (only those who have one). Small table — fine to scan.</summary>
    public async Task<Dictionary<Guid, string>> GetManyAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var want = userIds.Select(u => u.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, string>();
        if (want.Count == 0) return result;

        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"UserId\", \"DataUrl\" FROM \"UserAvatars\"";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (want.Contains(id) && Guid.TryParse(id, out var guid))
                    result[guid] = reader.GetString(1);
            }
        }
        finally { if (opened) await conn.CloseAsync(); }
        return result;
    }

    /// <summary>Avatars for every member of an account the caller belongs to (for showing pictures in member lists).</summary>
    public async Task<Dictionary<Guid, string>> GetForAccountAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.Include(a => a.Members)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Account not found.");
        if (!account.IsContributor(userId))
            throw new ApiException(StatusCodes.Status403Forbidden, "You don't have access to that account.");
        return await GetManyAsync(account.Members.Select(m => m.UserId), ct);
    }

    public async Task SetAsync(Guid userId, string? dataUrl, CancellationToken ct = default)
    {
        var trimmed = string.IsNullOrWhiteSpace(dataUrl) ? null : dataUrl.Trim();
        if (trimmed is { Length: > MaxLength })
            throw new ApiException(StatusCodes.Status400BadRequest, "That image is too large. Pick a smaller picture.");
        // Only an inline image or a trusted provider picture — never an arbitrary external URL, which would render
        // as an <img src> and beacon to shared-account members (tracking/deanonymisation).
        if (trimmed is not null && !IsAcceptableAvatar(trimmed))
            throw new ApiException(StatusCodes.Status400BadRequest, "That isn't a supported picture.");

        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            if (trimmed is null)
            {
                cmd.CommandText = "DELETE FROM \"UserAvatars\" WHERE \"UserId\" = @uid";
                AddParam(cmd, "@uid", userId.ToString());
            }
            else
            {
                cmd.CommandText =
                    "INSERT INTO \"UserAvatars\" (\"UserId\", \"DataUrl\") VALUES (@uid, @data) " +
                    "ON CONFLICT (\"UserId\") DO UPDATE SET \"DataUrl\" = @data";
                AddParam(cmd, "@uid", userId.ToString());
                AddParam(cmd, "@data", trimmed);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    // Hosts we adopt profile pictures from during external sign-in (Google / Facebook). Suffix-matched so regional
    // shards (lh3..lh6.googleusercontent.com, *.fbcdn.net, platform-lookaside.fbsbx.com) are covered without listing each.
    private static readonly string[] TrustedAvatarHosts =
    [
        "googleusercontent.com",
        "fbcdn.net",
        "fbsbx.com",
        "graph.facebook.com",
    ];

    /// <summary>An avatar is acceptable only if it's an inline image (<c>data:image/*</c>) or an https picture from a
    /// trusted sign-in provider host. Any other external URL is rejected — rendered as an <c>&lt;img src&gt;</c> it would
    /// beacon the viewer's IP to an arbitrary server and could deanonymise shared-account members (BACKLOG P0 #5).</summary>
    private static bool IsAcceptableAvatar(string value)
    {
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host;
        return TrustedAvatarHosts.Any(h =>
            host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
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
