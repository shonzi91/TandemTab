namespace FinApp.Shared.UI.Services;

/// <summary>Where the FinApp sync server lives. Set once at startup by the host (MAUI/WASM).</summary>
public sealed class ClientOptions
{
    public required string BaseUrl { get; init; }
}

/// <summary>Persists the auth tokens across launches. Implemented per host (MAUI uses SecureStorage).
/// Holds a short-lived access token and a long-lived refresh token; <see cref="ClearAsync"/> drops both.</summary>
public interface ITokenStore
{
    Task<string?> GetAsync();
    Task SetAsync(string token);
    Task<string?> GetRefreshAsync();
    Task SetRefreshAsync(string refreshToken);
    Task ClearAsync();
}
