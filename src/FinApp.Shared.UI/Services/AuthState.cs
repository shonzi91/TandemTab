using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Tracks the signed-in user and bearer token for the session. Restores a saved token on startup,
/// hands the token to the <see cref="FinAppApiClient"/>, and raises <see cref="Changed"/> so the UI
/// can switch between the auth screen and the app.
/// </summary>
public sealed class AuthState
{
    private readonly FinAppApiClient _api;
    private readonly ITokenStore _tokens;

    public AuthState(FinAppApiClient api, ITokenStore tokens)
    {
        _api = api;
        _tokens = tokens;
        // Let the API client renew an expired access token mid-session, transparently.
        _api.OnUnauthorized = RefreshAsync;
    }

    public UserDto? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;
    public Guid UserId => CurrentUser?.Id ?? Guid.Empty;

    public event Action? Changed;

    // Single-flight guard so concurrent 401s trigger at most one refresh (rotation makes double-refresh unsafe).
    private readonly object _refreshGate = new();
    private Task<bool>? _refreshInFlight;

    /// <summary>Restore a persisted session (validates the token via /me). Safe to call once at startup.
    /// If the access token has expired, the API client renews it via the refresh token automatically.</summary>
    public async Task<bool> TryRestoreAsync()
    {
        var token = await _tokens.GetAsync();
        var refresh = await _tokens.GetRefreshAsync();
        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(refresh)) return false;

        _api.Token = token;
        try
        {
            // If the access token is missing/expired but a refresh token exists, renew before calling /me.
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(refresh) && !await RefreshAsync(null))
                return false;
            CurrentUser = await _api.MeAsync();
            Changed?.Invoke();
            return true;
        }
        catch
        {
            await SignOutAsync();
            return false;
        }
    }

    /// <summary>Complete an external (Google/Facebook) sign-in by exchanging the one-time code from the redirect
    /// URL for real session tokens (access + refresh), which are then stored like a normal login.</summary>
    public async Task<bool> SignInWithCodeAsync(string code)
    {
        try
        {
            await ApplyAsync(() => _api.ExchangeCodeAsync(code));
            return true;
        }
        catch
        {
            await SignOutAsync();
            return false;
        }
    }

    public Task RegisterAsync(string username, string email, string password) =>
        ApplyAsync(() => _api.RegisterAsync(new RegisterRequest(username, email, password)));

    /// <summary>Attempt a password login. If the account has 2FA on, no tokens are issued yet — the returned
    /// outcome carries a ticket to complete via <see cref="CompleteTwoFactorLoginAsync"/>.</summary>
    public async Task<LoginOutcome> LogInAsync(string usernameOrEmail, string password)
    {
        var result = await _api.LoginAsync(new LoginRequest(usernameOrEmail, password));
        if (result.TwoFactorRequired)
            return new LoginOutcome(SignedIn: false, TwoFactorTicket: result.TwoFactorTicket);
        await ApplyAuthResponseAsync(result.Auth!);
        return new LoginOutcome(SignedIn: true, TwoFactorTicket: null);
    }

    /// <summary>Finish a 2FA-gated login with the ticket from <see cref="LogInAsync"/> plus a TOTP/recovery code.</summary>
    public Task CompleteTwoFactorLoginAsync(string ticket, string code) =>
        ApplyAsync(() => _api.TwoFactorLoginAsync(ticket, code));

    public async Task SignOutAsync()
    {
        // Best-effort: revoke the refresh token server-side so it can't be reused.
        var refresh = await _tokens.GetRefreshAsync();
        if (!string.IsNullOrEmpty(refresh))
            try { await _api.LogoutAsync(refresh); } catch { /* offline / already gone — clear locally anyway */ }

        CurrentUser = null;
        _api.Token = null;
        await _tokens.ClearAsync();
        Changed?.Invoke();
    }

    /// <summary>
    /// Renew the access token using the stored refresh token. Wired into <see cref="FinAppApiClient.OnUnauthorized"/>
    /// so an expired access token is refreshed transparently. Single-flighted: concurrent 401s share one refresh,
    /// and a request that already failed with a since-rotated token just retries with the new one (no double-refresh,
    /// which would trip the server's reuse detection). Returns true when a usable access token is in place.
    /// </summary>
    private Task<bool> RefreshAsync(string? tokenThatFailed)
    {
        // A concurrent refresh already replaced the token this request used — just retry with the new one.
        if (!string.IsNullOrEmpty(_api.Token) && tokenThatFailed is not null && _api.Token != tokenThatFailed)
            return Task.FromResult(true);

        lock (_refreshGate)
        {
            return _refreshInFlight ??= RefreshCoreAsync();
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        try
        {
            var refresh = await _tokens.GetRefreshAsync();
            if (string.IsNullOrEmpty(refresh)) return false;

            var auth = await _api.RefreshAsync(refresh);
            _api.Token = auth.Token;
            await _tokens.SetAsync(auth.Token);
            if (!string.IsNullOrEmpty(auth.RefreshToken))
                await _tokens.SetRefreshAsync(auth.RefreshToken);
            return true;
        }
        catch
        {
            return false;  // refresh token invalid/expired — caller surfaces the 401 and the user re-logs in
        }
        finally
        {
            lock (_refreshGate) { _refreshInFlight = null; }
        }
    }

    /// <summary>Re-fetch the signed-in user's profile (e.g. after enabling 2FA or verifying email).</summary>
    public async Task RefreshProfileAsync()
    {
        if (!IsAuthenticated) return;
        try { CurrentUser = await _api.MeAsync(); Changed?.Invoke(); } catch { /* best effort */ }
    }

    /// <summary>Update the signed-in user's avatar in memory (after uploading it to the server).</summary>
    public void SetAvatar(string? dataUrl)
    {
        if (CurrentUser is null) return;
        CurrentUser = CurrentUser with { Avatar = dataUrl };
        Changed?.Invoke();
    }

    private async Task ApplyAsync(Func<Task<AuthResponse>> call) => await ApplyAuthResponseAsync(await call());

    private async Task ApplyAuthResponseAsync(AuthResponse auth)
    {
        _api.Token = auth.Token;
        await _tokens.SetAsync(auth.Token);
        if (!string.IsNullOrEmpty(auth.RefreshToken))
            await _tokens.SetRefreshAsync(auth.RefreshToken);
        CurrentUser = new UserDto(auth.UserId, auth.Username, auth.Email);
        Changed?.Invoke();
        // Pull the full profile (incl. avatar) in the background; ignore failures (we're already signed in).
        try { CurrentUser = await _api.MeAsync(); Changed?.Invoke(); } catch { /* best effort */ }
    }
}

/// <summary>Outcome of a password login: either signed in, or a 2FA code is needed (carrying the ticket).</summary>
public sealed record LoginOutcome(bool SignedIn, string? TwoFactorTicket);
