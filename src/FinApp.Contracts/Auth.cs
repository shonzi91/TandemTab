namespace FinApp.Contracts;

/// <summary>Sign-up payload. The server hashes <see cref="Password"/> and never stores it in plaintext.</summary>
public record RegisterRequest(string Username, string Email, string Password);

/// <summary>Sign-in payload; <see cref="UsernameOrEmail"/> accepts either identifier.</summary>
public record LoginRequest(string UsernameOrEmail, string Password);

/// <summary>Result of register/login: a short-lived access token plus the authenticated user's identity.
/// <see cref="RefreshToken"/> is a long-lived opaque token used to obtain a fresh access token via
/// <c>/auth/refresh</c> (null for flows that don't issue one, e.g. legacy callers).</summary>
public record AuthResponse(string Token, Guid UserId, string Username, string Email, DateTimeOffset ExpiresAt, string? RefreshToken = null);

/// <summary>Exchange a valid refresh token for a new access token (and a rotated refresh token).</summary>
public record RefreshRequest(string RefreshToken);

/// <summary>Revoke a refresh token on sign-out so it can't be used again.</summary>
public record LogoutRequest(string RefreshToken);

/// <summary>Exchange a one-time external-sign-in code (from the OAuth redirect) for real session tokens.</summary>
public record ExchangeCodeRequest(string Code);

/// <summary>Result of a login attempt. When <see cref="TwoFactorRequired"/> is true the password was correct but a
/// second factor is needed: submit <see cref="TwoFactorTicket"/> plus a code to <c>/auth/2fa</c>. Otherwise
/// <see cref="Auth"/> holds the issued tokens.</summary>
public record LoginResponse(bool TwoFactorRequired, AuthResponse? Auth = null, string? TwoFactorTicket = null);

/// <summary>Complete a 2FA-gated login: the ticket from the login step plus a TOTP or recovery code.</summary>
public record TwoFactorLoginRequest(string Ticket, string Code);

/// <summary>Enrollment material for setting up an authenticator app: the Base32 secret, the otpauth:// URI, and a
/// ready-to-render QR image of that URI as a data URL (<c>data:image/png;base64,…</c>) for scanning.</summary>
public record TwoFactorSetupDto(string Secret, string OtpauthUri, string QrImage = "");

/// <summary>Confirm 2FA enrollment (or disable) with a current code.</summary>
public record TwoFactorCodeRequest(string Code);

/// <summary>The one-time recovery codes handed out when 2FA is enabled — shown once, stored by the user.</summary>
public record TwoFactorRecoveryDto(string[] RecoveryCodes);

/// <summary>A user as seen over the wire (never includes the password hash). <see cref="Avatar"/> is a data-URL profile picture.
/// <see cref="Provider"/> names the external sign-in provider (e.g. "google") when the user signed up that way — null for
/// password users; <see cref="IsExternal"/> is the convenience flag used to hide the password-change UI.</summary>
public record UserDto(Guid Id, string Username, string Email, string? Avatar = null, string? Provider = null,
    bool EmailVerified = false, bool TwoFactorEnabled = false, DateTimeOffset? PendingDeletionAt = null,
    bool IsAdmin = false, bool MonetizationEnabled = false, string Plan = "unlimited", bool ProBadge = false)
{
    public bool IsExternal => !string.IsNullOrEmpty(Provider);

    /// <summary>The account is scheduled for deletion (soft-deleted); the user can still cancel until this passes.</summary>
    public bool DeletionPending => PendingDeletionAt is not null;
}

/// <summary>Request to delete the signed-in user's whole account. When 2FA is on, <see cref="TwoFactorCode"/> is required.</summary>
public record DeleteAccountRequest(string? TwoFactorCode = null);

/// <summary>Set (or clear, with null) the signed-in user's profile picture. <see cref="DataUrl"/> is a small data-URL image.</summary>
public record SetAvatarRequest(string? DataUrl);

/// <summary>Change the signed-in user's password (the server verifies <see cref="CurrentPassword"/> first).</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Request a password-reset link for a username or email. The server always responds the same way, so it
/// never reveals whether the identifier matched an account.</summary>
public record ForgotPasswordRequest(string Identifier);

/// <summary>Set a new password using the one-time token from a reset link.</summary>
public record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>Which external sign-in providers the server has configured (controls which buttons the client shows).</summary>
public record ExternalProvidersDto(bool Google, bool Facebook);
