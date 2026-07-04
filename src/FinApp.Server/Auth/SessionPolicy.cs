using System.Data;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinApp.Server.Auth;

/// <summary>
/// Decides how long a user's session (refresh token) should live. Users who handle <b>sensitive data</b> — 2FA
/// enabled, or a linked bank in any account they belong to — get the strict <see cref="JwtOptions.RefreshTokenDays"/>
/// window; everyone else gets the much longer <see cref="JwtOptions.RefreshTokenDaysRelaxed"/> so casual budgeting
/// isn't gated behind frequent logins. Because the lifetime is recomputed on every issue <i>and</i> rotation, a user
/// who later adds 2FA or a bank steps down to the strict window on their next refresh — no forced logout needed.
/// </summary>
public sealed class SessionPolicy(FinAppDbContext db, TwoFactorService twoFactor, IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>Days a newly-issued refresh token should live for this user, based on their current sensitivity.</summary>
    public async Task<int> RefreshDaysForAsync(Guid userId, CancellationToken ct = default)
    {
        if (await twoFactor.IsEnabledAsync(userId, ct)) return _options.RefreshTokenDays;
        if (await HasLinkedBankAsync(userId, ct)) return _options.RefreshTokenDays;
        return _options.RefreshTokenDaysRelaxed;
    }

    /// <summary>True if any account the user belongs to has a linked bank connection.</summary>
    private async Task<bool> HasLinkedBankAsync(Guid userId, CancellationToken ct)
    {
        var accountIds = await db.Accounts
            .Where(a => a.Members.Any(m => m.UserId == userId))
            .Select(a => a.Id).ToListAsync(ct);
        if (accountIds.Count == 0) return false;

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            var names = accountIds.Select((_, i) => $"@a{i}").ToList();
            cmd.CommandText =
                $"SELECT 1 FROM \"BankConnections\" WHERE \"Status\" = 'Linked' AND \"AccountId\" IN ({string.Join(",", names)}) LIMIT 1";
            for (var i = 0; i < accountIds.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@a{i}";
                p.Value = accountIds[i].ToString();
                cmd.Parameters.Add(p);
            }
            try { return await cmd.ExecuteScalarAsync(ct) is not null; }
            catch { return false; }   // BankConnections table not created yet on a fresh db
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
