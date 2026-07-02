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

    /// <summary>Refresh-token lifetime. Long-lived so users stay signed in across sessions, but every use
    /// rotates it (see <see cref="RefreshTokenService"/>) and any reuse revokes the whole family.</summary>
    public int RefreshTokenDays { get; set; } = 30;
}
