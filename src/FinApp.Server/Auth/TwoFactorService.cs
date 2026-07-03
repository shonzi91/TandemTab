using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using FinApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>
/// Manages optional two-factor authentication (TOTP) per user.
///
/// Enrollment is two-step: <see cref="BeginEnrollAsync"/> stores an <i>unconfirmed</i> secret and returns it
/// (as an <c>otpauth://</c> URI) for the user to add to their authenticator app; <see cref="ConfirmAsync"/>
/// then verifies a live code before flipping 2FA on and handing back one-time recovery codes. 2FA counts as
/// "enabled" only once confirmed. At login, <see cref="VerifyAsync"/> accepts either a current TOTP code or an
/// unused recovery code.
///
/// State lives in <c>TwoFactor</c> (UserId → Secret, ConfirmedAt) and <c>TwoFactorRecoveryCodes</c> (hashed,
/// single-use). Same migration-free idempotent-table pattern as <see cref="RefreshTokenService"/>.
/// </summary>
public sealed class TwoFactorService(FinAppDbContext db)
{
    private const string Issuer = "TandemTab";
    private const int RecoveryCodeCount = 10;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"TwoFactor\" (" +
            "\"UserId\" text PRIMARY KEY, \"Secret\" text NOT NULL, \"ConfirmedAt\" text NULL)", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"TwoFactorRecoveryCodes\" (" +
            "\"Id\" text PRIMARY KEY, \"UserId\" text NOT NULL, \"CodeHash\" text NOT NULL, \"UsedAt\" text NULL)", ct);
    }

    /// <summary>Is 2FA confirmed (active) for this user?</summary>
    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default) =>
        await ConfirmedAtAsync(userId, ct) is not null;

    /// <summary>Start enrollment: (re)generate an unconfirmed secret and return it plus the otpauth URI to scan.</summary>
    public async Task<(string Secret, string OtpauthUri)> BeginEnrollAsync(Guid userId, string account, CancellationToken ct = default)
    {
        var secret = Totp.GenerateSecret();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"TwoFactor\" (\"UserId\", \"Secret\", \"ConfirmedAt\") VALUES (@uid, @secret, NULL) " +
                "ON CONFLICT (\"UserId\") DO UPDATE SET \"Secret\" = @secret, \"ConfirmedAt\" = NULL";
            AddParam(cmd, "@uid", userId.ToString());
            AddParam(cmd, "@secret", secret);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return (secret, Totp.BuildOtpauthUri(secret, Issuer, account));
    }

    /// <summary>Confirm enrollment with a live code. On success, activates 2FA and returns fresh recovery codes.</summary>
    public async Task<string[]?> ConfirmAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            var secret = await SecretAsync(conn, userId, ct);
            if (secret is null || !Totp.Verify(secret, code)) return null;

            await using (var confirm = conn.CreateCommand())
            {
                confirm.CommandText = "UPDATE \"TwoFactor\" SET \"ConfirmedAt\" = @at WHERE \"UserId\" = @uid";
                AddParam(confirm, "@at", Iso(DateTimeOffset.UtcNow));
                AddParam(confirm, "@uid", userId.ToString());
                await confirm.ExecuteNonQueryAsync(ct);
            }
            return await ResetRecoveryCodesAsync(conn, userId, ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Verify a login challenge: accept a current TOTP code or consume an unused recovery code.</summary>
    public async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            var confirmedSecret = await SecretAsync(conn, userId, ct, confirmedOnly: true);
            if (confirmedSecret is not null && Totp.Verify(confirmedSecret, code)) return true;
            return await TryConsumeRecoveryCodeAsync(conn, userId, code, ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    /// <summary>Turn 2FA off entirely (drops the secret and any recovery codes).</summary>
    public async Task DisableAsync(Guid userId, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using (var d1 = conn.CreateCommand())
            {
                d1.CommandText = "DELETE FROM \"TwoFactor\" WHERE \"UserId\" = @uid";
                AddParam(d1, "@uid", userId.ToString());
                await d1.ExecuteNonQueryAsync(ct);
            }
            await using var d2 = conn.CreateCommand();
            d2.CommandText = "DELETE FROM \"TwoFactorRecoveryCodes\" WHERE \"UserId\" = @uid";
            AddParam(d2, "@uid", userId.ToString());
            await d2.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private async Task<DateTimeOffset?> ConfirmedAtAsync(Guid userId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"ConfirmedAt\" FROM \"TwoFactor\" WHERE \"UserId\" = @uid";
            AddParam(cmd, "@uid", userId.ToString());
            return (await cmd.ExecuteScalarAsync(ct)) is string s
                ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static async Task<string?> SecretAsync(System.Data.Common.DbConnection conn, Guid userId, CancellationToken ct, bool confirmedOnly = false)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Secret\", \"ConfirmedAt\" FROM \"TwoFactor\" WHERE \"UserId\" = @uid";
        AddParam(cmd, "@uid", userId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (confirmedOnly && await reader.IsDBNullAsync(1, ct)) return null;
        return reader.GetString(0);
    }

    private static async Task<string[]> ResetRecoveryCodesAsync(System.Data.Common.DbConnection conn, Guid userId, CancellationToken ct)
    {
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM \"TwoFactorRecoveryCodes\" WHERE \"UserId\" = @uid";
            AddParam(del, "@uid", userId.ToString());
            await del.ExecuteNonQueryAsync(ct);
        }

        var codes = new string[RecoveryCodeCount];
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var display = FormatRecovery(RandomNumberGenerator.GetBytes(10));  // 10 chars, grouped XXXXX-XXXXX
            codes[i] = display;
            await using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO \"TwoFactorRecoveryCodes\" (\"Id\", \"UserId\", \"CodeHash\") VALUES (@id, @uid, @hash)";
            AddParam(ins, "@id", Guid.NewGuid().ToString());
            AddParam(ins, "@uid", userId.ToString());
            // Store the hash of the alphanumeric core so display grouping doesn't affect matching.
            AddParam(ins, "@hash", Hash(Core(display)));
            await ins.ExecuteNonQueryAsync(ct);
        }
        return codes;
    }

    private static async Task<bool> TryConsumeRecoveryCodeAsync(System.Data.Common.DbConnection conn, Guid userId, string code, CancellationToken ct)
    {
        var core = Core(code ?? "");
        if (core.Length == 0) return false;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE \"TwoFactorRecoveryCodes\" SET \"UsedAt\" = @at " +
            "WHERE \"UserId\" = @uid AND \"CodeHash\" = @hash AND \"UsedAt\" IS NULL";
        AddParam(cmd, "@at", Iso(DateTimeOffset.UtcNow));
        AddParam(cmd, "@uid", userId.ToString());
        AddParam(cmd, "@hash", Hash(core));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // Recovery codes are shown grouped (XXXXX-XXXXX) but matched on their uppercase alphanumeric core.
    private static string Core(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string FormatRecovery(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no ambiguous 0/O/1/I
        var s = new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
        return $"{s[..5]}-{s[5..]}"; // 10 chars grouped for readability
    }

    private static string Hash(string value) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

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
