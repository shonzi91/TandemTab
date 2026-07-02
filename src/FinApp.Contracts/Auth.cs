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

/// <summary>A user as seen over the wire (never includes the password hash). <see cref="Avatar"/> is a data-URL profile picture.
/// <see cref="Provider"/> names the external sign-in provider (e.g. "google") when the user signed up that way — null for
/// password users; <see cref="IsExternal"/> is the convenience flag used to hide the password-change UI.</summary>
public record UserDto(Guid Id, string Username, string Email, string? Avatar = null, string? Provider = null)
{
    public bool IsExternal => !string.IsNullOrEmpty(Provider);
}

/// <summary>Set (or clear, with null) the signed-in user's profile picture. <see cref="DataUrl"/> is a small data-URL image.</summary>
public record SetAvatarRequest(string? DataUrl);

/// <summary>Change the signed-in user's password (the server verifies <see cref="CurrentPassword"/> first).</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Which external sign-in providers the server has configured (controls which buttons the client shows).</summary>
public record ExternalProvidersDto(bool Google, bool Facebook);
