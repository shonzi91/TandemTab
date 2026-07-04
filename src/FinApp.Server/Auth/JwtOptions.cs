namespace FinApp.Server.Auth;

/// <summary>JWT signing/validation settings, bound from the "Jwt" config section. Defaults are dev-only.</summary>
public sealed class JwtOptions
{
    public string Key { get; set; } = "dev-only-finapp-signing-key-change-me-in-production-please";
    public string Issuer { get; set; } = "FinApp";
    public string Audience { get; set; } = "FinApp";

    /// <summary>Access-token lifetime. Kept short now that refresh tokens exist: a leaked access token
    /// is only useful for this window, and the client refreshes transparently.</summary>
    public int ExpiryHours { get; set; } = 2;

    /// <summary>Refresh-token lifetime for users who handle <b>sensitive data</b> (2FA on, or a linked bank).
    /// Long-lived but every use rotates it (see <see cref="RefreshTokenService"/>) and any reuse revokes the family.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Refresh-token lifetime for <b>low-sensitivity</b> users (no 2FA, no linked bank): a much longer,
    /// sliding "stay signed in" window so casual budgeting isn't gated behind frequent logins. Users step up to
    /// <see cref="RefreshTokenDays"/> automatically on their next token rotation once they add 2FA or a bank
    /// (see <see cref="SessionPolicy"/>).</summary>
    public int RefreshTokenDaysRelaxed { get; set; } = 180;
}
