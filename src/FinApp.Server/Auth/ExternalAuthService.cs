using System.Net.Http.Headers;
using System.Text.Json;
using FinApp.Server.Infrastructure;

namespace FinApp.Server.Auth;

/// <summary>
/// Manual OAuth 2.0 authorization-code flow for Google and Facebook sign-in. We don't use ASP.NET's
/// cookie-based external auth handlers — this app is JWT-only — so we drive the handshake by hand and,
/// on success, mint our own JWT (see <see cref="AuthService.FindOrCreateExternalUserAsync"/>).
/// A provider is "enabled" only when its client id + secret are configured (Auth:Google / Auth:Facebook),
/// so the feature stays inert until credentials are supplied.
/// </summary>
public sealed class ExternalAuthService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ExternalAuthService> log)
{
    public bool IsEnabled(string provider) =>
        !string.IsNullOrWhiteSpace(ClientId(provider)) && !string.IsNullOrWhiteSpace(ClientSecret(provider));

    private string? ClientId(string p) => p switch
    {
        "google" => config["Auth:Google:ClientId"],
        "facebook" => config["Auth:Facebook:AppId"],
        _ => null,
    };

    private string? ClientSecret(string p) => p switch
    {
        "google" => config["Auth:Google:ClientSecret"],
        "facebook" => config["Auth:Facebook:AppSecret"],
        _ => null,
    };

    /// <summary>The provider's consent page URL to redirect the browser to.</summary>
    public string BuildAuthorizeUrl(string provider, string redirectUri, string state)
    {
        var clientId = ClientId(provider)!;
        return provider switch
        {
            "google" =>
                "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&scope=" + Uri.EscapeDataString("openid email profile") +
                $"&state={Uri.EscapeDataString(state)}",
            "facebook" =>
                "https://www.facebook.com/v19.0/dialog/oauth?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&scope=email" +
                $"&state={Uri.EscapeDataString(state)}",
            _ => throw new BadRequestException("Unknown sign-in provider."),
        };
    }

    /// <summary>Exchange the auth code for a token and fetch the user's email, name and picture URL.</summary>
    public async Task<(string Email, string? Name, string? Picture)> CompleteAsync(string provider, string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        var accessToken = await ExchangeCodeAsync(http, provider, code, redirectUri, ct);
        return await FetchUserAsync(http, provider, accessToken, ct);
    }

    /// <summary>
    /// Download a provider profile picture and return it as a <c>data:image/…;base64,…</c> URL, or null if it can't
    /// be had cheaply and safely.
    /// <para>
    /// ⚠️ <b>Why we don't just store the provider's URL.</b> A browser renders
    /// <c>&lt;img src="https://lh3.googleusercontent.com/…"&gt;</c> happily, so the web app always looked fine — but
    /// that URL is the *only* thing a Google sign-in ever produced, and no other client can render it. The Android
    /// app decodes avatars straight from the data URL (SettingsSheet.decodeDataUrlImage) and has no image loader at
    /// all, so it silently fell back to the coloured initial: signing in with Google gave you no picture on the
    /// phone, ever. Inlining here fixes every client at once and needs none of them to grow an HTTP image path.
    /// </para>
    /// It also closes the beacon the stored URL opened: every viewer of a shared account fetched that image from
    /// Google, handing Google their IP — the exact leak <see cref="AvatarService.IsTrustedProviderPicture"/>'s
    /// allowlist exists to bound.
    /// <para>⚠️ Best effort by design. The caller keeps signing the user in when this returns null: a missing
    /// picture must never cost somebody their login.</para>
    /// </summary>
    public async Task<string?> FetchInlineAvatarAsync(string pictureUrl, CancellationToken ct)
    {
        // ⚠️ Every `return null` below is logged, and that is not decoration. This whole path used to be silent:
        // it runs once, inside somebody else's sign-in, and its only visible symptom is a picture that isn't
        // there — which looks exactly like "this user never had one". When a picture was reported missing there
        // was no signal anywhere to say which branch had fired, or whether it had run at all. A best-effort path
        // that swallows its own failures needs to say so somewhere; Information for the outcomes we expect,
        // Warning for the ones that mean something is actually wrong.
        //
        // Deliberately no user id and no email here — the host and the reason are what diagnose this, and the
        // URL is the user's own profile picture. `pictureUrl` is logged only for the untrusted-host refusal,
        // where the whole point is to see what was refused.

        // Only ever reach out to a host we already trust to hold a profile picture — this is a server-side fetch of
        // a URL that arrived over the wire, so the allowlist is doing SSRF duty here, not just privacy duty.
        if (!AvatarService.IsTrustedProviderPicture(pictureUrl))
        {
            log.LogWarning("Avatar adoption: refused an untrusted provider picture URL ({Url}).", pictureUrl);
            return null;
        }
        try
        {
            var http = httpFactory.CreateClient();
            // Short leash: this sits inside the OAuth callback, between the user and their own sign-in.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await http.GetAsync(pictureUrl, timeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Avatar adoption: provider returned {Status} for the picture.", (int)resp.StatusCode);
                return null;
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Avatar adoption: picture had content type {MediaType}, not an image.", mediaType ?? "(none)");
                return null;
            }
            // Base64 inflates by 4/3 and AvatarService caps the stored string at 400 KB, so anything past ~250 KB
            // raw could not be stored anyway. A provider avatar is a few KB; this is a bound, not a resize.
            if (resp.Content.Headers.ContentLength is > MaxAvatarBytes)
            {
                log.LogWarning("Avatar adoption: picture declared {Bytes} bytes, over the {Max} cap.",
                    resp.Content.Headers.ContentLength, MaxAvatarBytes);
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(timeout.Token);
            if (bytes.Length is 0 or > MaxAvatarBytes)
            {
                log.LogWarning("Avatar adoption: picture body was {Bytes} bytes (empty, or over the {Max} cap).",
                    bytes.Length, MaxAvatarBytes);
                return null;
            }
            log.LogInformation("Avatar adoption: inlined a {MediaType} picture of {Bytes} bytes.", mediaType, bytes.Length);
            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            // timeout, DNS, 429, truncated body — the sign-in carries on without a picture
            log.LogWarning(ex, "Avatar adoption: the picture download failed; signing in without one.");
            return null;
        }
    }

    private const int MaxAvatarBytes = 250_000;

    private async Task<string> ExchangeCodeAsync(HttpClient http, string provider, string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId(provider)!,
            ["client_secret"] = ClientSecret(provider)!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        var tokenUrl = provider == "google"
            ? "https://oauth2.googleapis.com/token"
            : "https://graph.facebook.com/v19.0/oauth/access_token";

        using var resp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        if (!resp.IsSuccessStatusCode)
            throw new BadRequestException("Couldn't complete sign-in with the provider.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString() ?? throw new BadRequestException("No access token returned.")
            : throw new BadRequestException("No access token returned.");
    }

    private static async Task<(string Email, string? Name, string? Picture)> FetchUserAsync(HttpClient http, string provider, string accessToken, CancellationToken ct)
    {
        var userInfoUrl = provider == "google"
            ? "https://openidconnect.googleapis.com/v1/userinfo"
            : "https://graph.facebook.com/me?fields=name,email,picture.width(256)";

        using var req = new HttpRequestMessage(HttpMethod.Get, userInfoUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new BadRequestException("Couldn't read your profile from the provider.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("The provider didn't share an email address.");

        // Google returns a flat "picture" URL; Facebook nests it under picture.data.url.
        string? picture = null;
        if (root.TryGetProperty("picture", out var pic))
        {
            if (pic.ValueKind == JsonValueKind.String)
                picture = pic.GetString();
            else if (pic.ValueKind == JsonValueKind.Object && pic.TryGetProperty("data", out var d) && d.TryGetProperty("url", out var url))
                picture = url.GetString();
        }
        return (email!, name, picture);
    }
}
