using FinApp.Shared.UI.Services;
using Microsoft.JSInterop;

namespace FinApp.App.Web;

/// <summary>
/// Persists the auth bearer token in the browser's <c>localStorage</c> via JS interop — the WASM
/// counterpart to MAUI's SecureStorage-backed token store. No external package needed.
/// </summary>
public sealed class WebTokenStore(IJSRuntime js) : ITokenStore
{
    private const string Key = "finapp-auth-token";
    private const string RefreshKey = "finapp-refresh-token";

    public async Task<string?> GetAsync()
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", Key); }
        catch { return null; }
    }

    public async Task SetAsync(string token)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", Key, token); }
        catch { /* storage unavailable — token simply won't persist across reloads */ }
    }

    public async Task<string?> GetRefreshAsync()
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", RefreshKey); }
        catch { return null; }
    }

    public async Task SetRefreshAsync(string refreshToken)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", RefreshKey, refreshToken); }
        catch { /* storage unavailable — token simply won't persist across reloads */ }
    }

    public async Task ClearAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.removeItem", Key); } catch { /* ignore */ }
        try { await js.InvokeVoidAsync("localStorage.removeItem", RefreshKey); } catch { /* ignore */ }
    }
}
