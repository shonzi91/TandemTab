using System.Text;
using FinApp.Contracts;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using FinApp.Server.Auth;
using FinApp.Server.BankSync;
using FinApp.Server.Infrastructure;
using FinApp.Server.Invitations;
using FinApp.Server.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;

// Register the SQLite (SQLCipher-capable) native provider once for the process.
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// Database provider: SQLite by default (local dev, tests, MAUI), Postgres in the cloud.
// To use Postgres set Database__Provider=Postgres and ConnectionStrings__FinApp=<Npgsql conn string>.
var usePostgres = string.Equals(builder.Configuration["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase);
var connectionString = builder.Configuration.GetConnectionString("FinApp")
                       ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "finapp-server.db")}";
builder.Services.AddDbContext<FinAppDbContext>(o =>
{
    if (usePostgres) o.UseNpgsql(NormalizePostgres(connectionString));
    else o.UseSqlite(connectionString);
});

// Accept either an Npgsql key-value string or a postgres:// URI (what Neon/Heroku/etc. hand out),
// since Npgsql itself only parses the key-value form.
static string NormalizePostgres(string cs)
{
    if (!cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        return cs; // already key-value

    var uri = new Uri(cs);
    var userInfo = uri.UserInfo.Split(':', 2);
    var b = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        Database = uri.AbsolutePath.Trim('/'),
        SslMode = SslMode.Require,
    };
    return b.ConnectionString;
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

// Refuse to start in production with the dev placeholder signing key. Set a real one via the
// Jwt__Key environment variable (>= 32 chars). The placeholder is fine for local development.
const string DevJwtKeyPlaceholder = "dev-only-finapp-signing-key-change-me-in-production-please";
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key == DevJwtKeyPlaceholder || jwt.Key.Length < 32))
{
    throw new InvalidOperationException(
        "Jwt:Key must be set to a real secret (>= 32 chars) outside Development. " +
        "Provide it via the Jwt__Key environment variable.");
}

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<AuthCodeService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.AddScoped<EmailVerificationService>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<SessionPolicy>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AvatarService>();
builder.Services.AddScoped<ExternalIdentityService>();
builder.Services.AddScoped<ConsentService>();
builder.Services.AddScoped<ExternalAuthService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ArchivedAccountsService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddScoped<AccountExportService>();
// Snapshot at-rest encryption: envelope-encrypt via Cloud KMS when a key is configured, else store plaintext
// (local dev / tests). Set Kms__KeyName=projects/…/locations/…/keyRings/…/cryptoKeys/… on Cloud Run to enable.
var kmsKeyName = builder.Configuration["Kms:KeyName"];
// Snapshots:CompressWrites gzips the payload inside the envelope (~7x smaller rows). Off by default: a server build
// that predates the ENC2 prefix mis-reads such rows as plaintext, so this must only be turned on once the build
// you'd roll back to can already read them. See EnvelopeSnapshotCipher for the two-phase rollout.
var compressSnapshots = builder.Configuration.GetValue("Snapshots:CompressWrites", false);
if (!string.IsNullOrWhiteSpace(kmsKeyName))
    builder.Services.AddSingleton<FinApp.Server.Accounts.ISnapshotCipher>(_ => new FinApp.Server.Accounts.KmsSnapshotCipher(kmsKeyName, compressSnapshots));
else
    builder.Services.AddSingleton<FinApp.Server.Accounts.ISnapshotCipher, FinApp.Server.Accounts.PassthroughSnapshotCipher>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<EnableBankingClient>();  // mints its own RS256 JWT per call; no shared state to cache
builder.Services.AddSingleton<BankDataProtector>(); // encrypts bank balances/transactions at rest (key from Jwt:Key)
builder.Services.AddSingleton<BankAccessPolicy>();  // MVP allowlist — bank sync limited to configured emails
builder.Services.AddScoped<BankSyncService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<SyncNotifier>();

// CORS for the Blazor WASM web host (different origin from the API in dev).
// Accept gzipped request bodies (see UseRequestDecompression below).
builder.Services.AddRequestDecompression();

// SignalR needs an explicit origin list + AllowCredentials (can't use AllowAnyOrigin with credentials).
const string WasmCorsPolicy = "wasm";
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? ["http://localhost:5080"];
builder.Services.AddCors(o => o.AddPolicy(WasmCorsPolicy, p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub"/"email" claim names as-issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // SignalR (WebSockets/SSE) can't use the Authorization header — read the token off the query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// Throttle sensitive endpoints (login/register) per client IP to blunt brute-force + abuse. Disabled in
// Development so the test suite (many rapid registrations) isn't throttled.
var throttleAuth = !builder.Environment.IsDevelopment();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => throttleAuth
        ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("dev"));
    // Invites are answered "No user named 'X'" (kept, so a typo is obvious in this collaboration app), so cap the
    // rate per IP to blunt username enumeration by scanning (BACKLOG P0 #6). Own bucket, so it doesn't share with auth.
    options.AddPolicy("invite", context => throttleAuth
        ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("dev"));
});

var app = builder.Build();

// Behind Cloud Run's TLS-terminating proxy the request reads as http; honour X-Forwarded-Proto so
// Request.Scheme is https (needed so the OAuth redirect_uri we build matches what providers expect).
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// Ensure the server DB schema is current on startup.
// SQLite uses the EF migrations; Postgres uses EnsureCreated (the migrations are SQLite-specific,
// and the cloud DB is provisioned fresh) so we build the schema straight from the model.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
    if (usePostgres) db.Database.EnsureCreated();
    else db.Database.Migrate();
    // Avatars live in a standalone table created idempotently (no EF migration; works on both providers).
    await scope.ServiceProvider.GetRequiredService<AvatarService>().EnsureSchemaAsync();
    // Bank-sync tables (connections + staged transactions) follow the same idempotent-create pattern.
    await scope.ServiceProvider.GetRequiredService<BankSyncService>().EnsureSchemaAsync();
    // External-identity marker table (which users signed up via Google/Facebook) — same pattern.
    await scope.ServiceProvider.GetRequiredService<ExternalIdentityService>().EnsureSchemaAsync();
    // Consent audit log (login / bank-link / bank-sync grants + withdrawals).
    await scope.ServiceProvider.GetRequiredService<ConsentService>().EnsureSchemaAsync();
    // Refresh-token store (rotation + reuse detection) — same idempotent-create pattern.
    await scope.ServiceProvider.GetRequiredService<RefreshTokenService>().EnsureSchemaAsync();
    // One-time auth codes for external sign-in (keeps session tokens out of the redirect URL).
    await scope.ServiceProvider.GetRequiredService<AuthCodeService>().EnsureSchemaAsync();
    // Email-verification state + one-time confirmation tokens.
    await scope.ServiceProvider.GetRequiredService<EmailVerificationService>().EnsureSchemaAsync();
    await scope.ServiceProvider.GetRequiredService<PasswordResetService>().EnsureSchemaAsync();
    // Two-factor (TOTP) secrets + recovery codes.
    await scope.ServiceProvider.GetRequiredService<TwoFactorService>().EnsureSchemaAsync();
    // Archived-accounts table + purge anything past its 30-day grace window on startup.
    var archives = scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>();
    await archives.EnsureSchemaAsync();
    await archives.PurgeExpiredAsync();
    // Pending user-deletion table + hard-delete any identity past its 30-day grace window on startup.
    var deletions = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
    await deletions.EnsureSchemaAsync();
    await deletions.PurgeDueAsync();
    // If snapshot encryption is configured, encrypt any rows still stored as plaintext (idempotent, no-op without KMS).
    try
    {
        var migrated = await scope.ServiceProvider.GetRequiredService<SnapshotService>().EncryptLegacyRowsAsync();
        if (migrated > 0) app.Logger.LogInformation("Snapshot encryption: encrypted {Count} legacy plaintext row(s).", migrated);
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Snapshot encryption migration failed at startup."); }
}

// Security response headers on everything (incl. static files + errors). Set before next() so they're
// applied before any body is written. CSP allows what Blazor WASM needs (wasm-unsafe-eval) and external
// image sources for bank/vendor logos + data-URL avatars; script-src keeps 'unsafe-inline' only because the
// static index.html has inline bootstrap scripts (tightening to hashes/nonces is a Tier-2 follow-up).
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    h["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";
    h["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data: https:; font-src 'self'; connect-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline'";

    // Translate our ApiException into a JSON problem response; log and mask everything else (no stack traces to clients).
    try
    {
        await next();
    }
    catch (ApiException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
    catch (Exception ex)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Global")
            .LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Something went wrong. Please try again." });
        }
    }
});

// One-origin hosting: serve the Blazor WASM client (_framework + wwwroot assets) as static files.
// Placed before auth so the app shell loads without a token.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The scoped-CSS bundle and the app shell have hash-less URLs, so browsers would cache them and
    // keep importing the previous build's (hashed) styles after a deploy. Force revalidation on those
    // entry files; the fingerprinted assets they pull in (_framework, _content/<hash>) stay cacheable.
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.EndsWith(".styles.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    }
});

// CORS is only needed when the web client runs on a separate origin (local two-terminal dev).
// In a one-origin deployment the client and API share an origin, so it's a no-op there.
if (app.Environment.IsDevelopment())
    app.UseCors(WasmCorsPolicy);

// Transparently gunzip request bodies sent with Content-Encoding: gzip (the client compresses anything large —
// chiefly the ~260KB account snapshot). Must run before anything reads the body, so it sits ahead of the
// endpoints; requests without the header pass straight through. Kestrel's max-request-body limit still applies
// to the *decompressed* stream, so a zip bomb can't buy extra headroom here.
app.UseRequestDecompression();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// --- Auth ----------------------------------------------------------------
var auth = app.MapGroup("/auth");
auth.MapPost("/register", async (RegisterRequest req, HttpContext http, AuthService svc, IConfiguration cfg, CancellationToken ct) =>
{
    var result = await svc.RegisterAsync(req, ct);
    // Send the confirmation email (best-effort — never fail the sign-up if email is down/unconfigured).
    // The sender logs any SMTP failure; we deliberately swallow it here so sign-up still succeeds and the
    // user can resend from the app.
    try { await svc.SendVerificationEmailAsync(result.UserId, result.Email, AppBaseUrl(http, cfg), ct); }
    catch { /* logged by EmailSender; sign-up must not fail on email problems */ }
    return Results.Ok(result);
}).RequireRateLimiting("auth");
auth.MapPost("/login", async (LoginRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.LoginAsync(req, ct))).RequireRateLimiting("auth");
// Complete a 2FA-gated login with the ticket from /auth/login plus a TOTP or recovery code.
auth.MapPost("/2fa", async (TwoFactorLoginRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.TwoFactorLoginAsync(req.Ticket, req.Code, ct))).RequireRateLimiting("auth");
// Exchange a refresh token for a new access token (rotates the refresh token). Rate-limited like login.
auth.MapPost("/refresh", async (RefreshRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.RefreshAsync(req.RefreshToken, ct))).RequireRateLimiting("auth");
// Revoke a refresh token on sign-out. Anonymous: possession of the token is the authorization.
auth.MapPost("/logout", async (LogoutRequest req, AuthService svc, CancellationToken ct) =>
{
    await svc.LogoutAsync(req.RefreshToken, ct);
    return Results.NoContent();
});
// Exchange a one-time external-sign-in code (from the OAuth redirect) for an access + refresh token.
auth.MapPost("/exchange", async (ExchangeCodeRequest req, AuthService svc, CancellationToken ct) =>
    Results.Ok(await svc.ExchangeAsync(req.Code, ct))).RequireRateLimiting("auth");
// Confirm an email address from the link in the verification email, then bounce back into the app.
auth.MapGet("/verify-email", async (string? token, AuthService svc, CancellationToken ct) =>
{
    var ok = !string.IsNullOrEmpty(token) && await svc.VerifyEmailAsync(token!, ct);
    return Results.Redirect(ok ? "/?emailVerified=1" : "/?emailVerified=0");
});
// Re-send the confirmation email to the signed-in user's current address.
auth.MapPost("/resend-verification", async (ClaimsPrincipal user, HttpContext http, AuthService svc, IConfiguration cfg, CancellationToken ct) =>
{
    await svc.SendVerificationEmailAsync(user.UserId(), user.Email(), AppBaseUrl(http, cfg), ct);
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("auth");

// --- Forgotten-password reset (anonymous) ---------------------------------
// Always succeeds regardless of whether the identifier matched an account (no enumeration); a real match gets a
// mailed link. Rate-limited like the rest of auth. Email failures are swallowed so we still don't leak existence.
auth.MapPost("/password/forgot", async (ForgotPasswordRequest req, HttpContext http, AuthService svc, IConfiguration cfg, CancellationToken ct) =>
{
    try { await svc.SendPasswordResetEmailAsync(req.Identifier, AppBaseUrl(http, cfg), ct); }
    catch { /* logged by EmailSender; never reveal success/failure to the caller */ }
    return Results.NoContent();
}).RequireRateLimiting("auth");
// Redeem the one-time token from the emailed link and set a new password. 400 on an invalid/expired token.
auth.MapPost("/password/reset", async (ResetPasswordRequest req, AuthService svc, CancellationToken ct) =>
{
    await svc.ResetPasswordAsync(req.Token, req.NewPassword, ct);
    return Results.NoContent();
}).RequireRateLimiting("auth");

// --- Two-factor (TOTP) management (signed-in) -----------------------------
// Begin enrollment: returns the secret + otpauth URI to add to an authenticator app.
auth.MapPost("/2fa/setup", async (ClaimsPrincipal user, TwoFactorService twoFactor, CancellationToken ct) =>
{
    var (secret, uri) = await twoFactor.BeginEnrollAsync(user.UserId(), user.Email(), ct);
    return Results.Ok(new TwoFactorSetupDto(secret, uri, QrDataUrl(uri)));
}).RequireAuthorization();
// Confirm enrollment with a live code; returns one-time recovery codes (shown once).
auth.MapPost("/2fa/confirm", async (TwoFactorCodeRequest req, ClaimsPrincipal user, TwoFactorService twoFactor, AuthService svc, CancellationToken ct) =>
{
    var codes = await twoFactor.ConfirmAsync(user.UserId(), req.Code, ct);
    if (codes is null)
        return Results.BadRequest(new { error = "That code isn't right. Check your authenticator app and try again." });
    // Also email the codes to a verified address so the user has a durable copy (shown only once on screen).
    try { await svc.EmailRecoveryCodesAsync(user.UserId(), user.Email(), codes, ct); } catch { /* logged by EmailSender */ }
    return Results.Ok(new TwoFactorRecoveryDto(codes));
}).RequireAuthorization().RequireRateLimiting("auth");
// Disable 2FA (requires a current code to prove possession of the second factor).
auth.MapPost("/2fa/disable", async (TwoFactorCodeRequest req, ClaimsPrincipal user, TwoFactorService twoFactor, CancellationToken ct) =>
{
    if (!await twoFactor.VerifyAsync(user.UserId(), req.Code, ct))
        return Results.BadRequest(new { error = "That code isn't right." });
    await twoFactor.DisableAsync(user.UserId(), ct);
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("auth");
auth.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal user, AuthService svc, CancellationToken ct) =>
{
    await svc.ChangePasswordAsync(user.UserId(), req, ct);
    return Results.NoContent();
}).RequireAuthorization();

// --- External sign-in (Google / Facebook), manual OAuth code flow ---------
auth.MapGet("/providers", (ExternalAuthService ext) =>
    Results.Ok(new ExternalProvidersDto(ext.IsEnabled("google"), ext.IsEnabled("facebook"))));

auth.MapGet("/external/{provider}", (string provider, HttpContext http, ExternalAuthService ext, IConfiguration cfg) =>
{
    if (!ext.IsEnabled(provider)) return Results.NotFound();
    var redirectUri = ExternalRedirectUri(http, cfg, provider);
    var state = Guid.NewGuid().ToString("N");
    http.Response.Cookies.Append("finapp_oauth_state", state, new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/",
    });
    return Results.Redirect(ext.BuildAuthorizeUrl(provider, redirectUri, state));
});

auth.MapGet("/external/{provider}/callback", async (string provider, string? code, string? state,
    HttpContext http, ExternalAuthService ext, AuthService authSvc, AuthCodeService authCodes,
    AvatarService avatars, ExternalIdentityService identities, IConfiguration cfg, CancellationToken ct) =>
{
    if (!ext.IsEnabled(provider) || string.IsNullOrEmpty(code)) return Results.Redirect("/?authError=1");
    var expectedState = http.Request.Cookies["finapp_oauth_state"];
    http.Response.Cookies.Delete("finapp_oauth_state");
    if (string.IsNullOrEmpty(state) || state != expectedState) return Results.Redirect("/?authError=1");
    try
    {
        var redirectUri = ExternalRedirectUri(http, cfg, provider);
        var (email, name, picture) = await ext.CompleteAsync(provider, code, redirectUri, ct);
        var userId = await authSvc.FindOrCreateExternalUserAsync(email, name, ct);
        await identities.MarkAsync(userId, provider, ct);   // so the UI can hide "change password" for them
        // Adopt the provider's profile picture only if the user hasn't set one of their own.
        if (!string.IsNullOrWhiteSpace(picture) && await avatars.GetAsync(userId, ct) is null)
            await avatars.SetAsync(userId, picture, ct);
        // Hand the SPA a one-time code (not a token) in the query string. The client POSTs it to /auth/exchange
        // for the real access + refresh token, keeping session tokens out of the URL/history/Referer.
        var authCode = await authCodes.IssueAsync(userId, ct);
        return Results.Redirect($"/?authCode={Uri.EscapeDataString(authCode)}");
    }
    catch { return Results.Redirect("/?authError=1"); }
});

// --- Consent (audit-logged: login / bank-link / bank-sync) ---------------
app.MapGet("/consent", async (string scope, Guid? accountId, ClaimsPrincipal user, ConsentService consent, CancellationToken ct) =>
{
    var latest = await consent.LatestAsync(user.UserId(), accountId, scope, ct);
    var active = latest is { Granted: true, PolicyVersion: ConsentService.PolicyVersion };
    return Results.Ok(new ConsentStatusDto(scope, active, latest?.At, ConsentService.PolicyVersion));
}).RequireAuthorization();

app.MapPost("/consent", async (RecordConsentRequest req, ClaimsPrincipal user, ConsentService consent, CancellationToken ct) =>
{
    await consent.RecordAsync(user.UserId(), req.AccountId, req.Scope, req.Granted, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/me", async (ClaimsPrincipal user, AvatarService avatars, ExternalIdentityService identities,
        EmailVerificationService emailVerification, TwoFactorService twoFactor, AccountDeletionService deletions, CancellationToken ct) =>
        Results.Ok(new UserDto(user.UserId(), user.Username(), user.Email(),
            await avatars.GetAsync(user.UserId(), ct), await identities.GetProviderAsync(user.UserId(), ct),
            EmailVerified: await emailVerification.IsVerifiedAsync(user.UserId(), user.Email(), ct),
            TwoFactorEnabled: await twoFactor.IsEnabledAsync(user.UserId(), ct),
            PendingDeletionAt: await deletions.ScheduledAtAsync(user.UserId(), ct))))
    .RequireAuthorization();

// Delete the signed-in user's whole account (soft delete: 30-day grace, then purged). 2FA-gated when enabled;
// blocked while they still own a shared account (transfer ownership first). Logging back in + cancel aborts it.
app.MapPost("/me/delete", async (DeleteAccountRequest req, ClaimsPrincipal user, AccountDeletionService deletions, CancellationToken ct) =>
{
    await deletions.RequestAsync(user.UserId(), req.TwoFactorCode, ct);
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("auth");

app.MapPost("/me/delete/cancel", async (ClaimsPrincipal user, AccountDeletionService deletions, CancellationToken ct) =>
{
    await deletions.CancelAsync(user.UserId(), ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPut("/me/avatar", async (SetAvatarRequest req, ClaimsPrincipal user, AvatarService avatars, CancellationToken ct) =>
{
    await avatars.SetAsync(user.UserId(), req.DataUrl, ct);
    return Results.NoContent();
}).RequireAuthorization();

// --- Accounts ------------------------------------------------------------
var accounts = app.MapGroup("/accounts").RequireAuthorization();

accounts.MapGet("", async (ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListForUserAsync(user.UserId(), ct)));

accounts.MapPost("", async (CreateAccountRequest req, ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(user.UserId(), user.Username(), req, ct)));

accounts.MapPut("/{id:guid}/name", async (Guid id, RenameAccountRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.RenameAsync(user.UserId(), id, req.Name, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.DeleteAsync(user.UserId(), id, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

// --- Membership: leave / remove / transfer / archive ---------------------
accounts.MapGet("/archived", async (ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListArchivedForUserAsync(user.UserId(), ct)));

accounts.MapPost("/{id:guid}/leave", async (Guid id, LeaveAccountRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var result = await svc.LeaveAsync(user.UserId(), id, req.NewOwnerUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());   // remaining members re-pull the new membership/owner
    return Results.Ok(new { result = result.ToString() });
});

accounts.MapDelete("/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.RemoveMemberAsync(user.UserId(), id, memberUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/transfer-ownership", async (Guid id, TransferOwnershipRequest req, ClaimsPrincipal user, AccountService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    await svc.TransferOwnershipAsync(user.UserId(), id, req.NewOwnerUserId, ct);
    await notifier.AccountChangedAsync(id, user.UserId());
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/reactivate", async (Guid id, ClaimsPrincipal user, AccountService svc, CancellationToken ct) =>
{
    await svc.ReactivateAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

// --- Account snapshot (full aggregate, opaque blob) ----------------------
accounts.MapGet("/{id:guid}/snapshot", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetAsync(user.UserId(), id, ct)));

// --- Computed reads (Option-A migration, docs/MOBILE.md) -----------------
// First read moved server-side: the Home balance-header figures, computed from the snapshot the server
// already loads. Reuses SnapshotService.GetAsync for the contributor auth + decrypt; the domain does the maths.
accounts.MapGet("/{id:guid}/overview", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(AccountOverviewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    if (account.CurrentPeriod is not { } period) return Results.Ok(AccountOverviewDto.Empty with { Currency = account.Currency });
    var ov = AccountOverview.For(account, period);
    return Results.Ok(new AccountOverviewDto(
        account.Currency, ov.Current.Amount, ov.Free.Amount, ov.Saved.Amount,
        ov.Spent.Amount, ov.Contributed.Amount, ov.BillsDue.Amount, ov.SafeAfterBills.Amount));
});

// The cash runway (first month the balance runs short, or null when there's no basis to project from).
accounts.MapGet("/{id:guid}/runway", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    // 204 when there's no runway to show (no snapshot, or no trustworthy basis to project from) — the UI
    // renders nothing in that case, which is a real state distinct from "the figures are zero".
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.NoContent();
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    if (AccountForecast.Runway(account) is not { } proj) return Results.NoContent();
    return Results.Ok(new RunwayDto(
        account.Currency, proj.Months.Count, proj.FirstShortfallMonth,
        proj.Months[0].Income, proj.Months[0].Spending,
        proj.Basis == CashFlowBasis.Recurring,
        account.Periods.Count(p => p.Status == PeriodStatus.Closed),
        proj.HasUnknownAmounts));
});

// The Home "on track for" targets — the debt-free date + each savings goal's date. 200 with an empty list when
// there's nothing to project (distinct from runway's 204: an empty target set is a normal, expected state).
accounts.MapGet("/{id:guid}/targets", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(TargetsDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var targets = AccountForecast.Targets(account)
        .Select(t => new TargetDto(t.Kind == TargetKind.DebtFree ? "debt-free" : "goal", t.Name, t.Icon, t.Months, t.Reached))
        .ToList();
    return Results.Ok(new TargetsDto(targets));
});

// The Home milestone tallies (earned / total / in-progress). The full localized catalogue stays client-side.
accounts.MapGet("/{id:guid}/milestones", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(MilestonesDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var c = new AchievementsService().Counts(account);
    return Results.Ok(new MilestonesDto(c.Earned, c.Total, c.InProgress));
});

// The Insights health read (latest period): the gauge score/band + savings + trend + breakdown numbers, plus the
// narrative as language-independent messages (code + args). The client owns the per-language templates — see InsightsDto.
accounts.MapGet("/{id:guid}/insights", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(InsightsDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    if (account.Periods.Count == 0) return Results.Ok(InsightsDto.Empty);
    var report = new InsightsService().Build(account, account.Periods.Count - 1);
    if (!report.HasData) return Results.Ok(InsightsDto.Empty);

    static string Dir(DeltaDir d) => d == DeltaDir.Up ? "up" : d == DeltaDir.Down ? "down" : "flat";
    static string Band(HealthBand b) => b == HealthBand.Healthy ? "healthy" : b == HealthBand.AtRisk ? "at-risk" : "average";
    static string ArgKind(InsightArgKind k) => k switch
    {
        InsightArgKind.Money => "money",
        InsightArgKind.Percent => "percent",
        InsightArgKind.Int => "int",
        _ => "text",
    };
    static InsightMessageDto Msg(InsightMessage m) =>
        new(m.Code, m.Args.Select(a => new InsightArgDto(ArgKind(a.Kind), a.Number, a.Text)).ToList());

    return Results.Ok(new InsightsDto(
        report.HasData, report.Score, report.ScoreDelta, Band(report.Band),
        report.SavingsRate, report.SavingsTarget, report.SavingsShortfall?.Amount,
        report.TrendUp, report.TrendAverage.Amount, report.TrendAvgFraction,
        Msg(report.Verdict),
        report.Summary.Select(Msg).ToList(),
        report.SavingsCritique.Select(Msg).ToList(),
        Msg(report.TrendNote),
        report.Signals.Select(s => new InsightSignalDto(
            s.Kind == SignalKind.Warn ? "warn" : s.Kind == SignalKind.Good ? "good" : "info",
            Msg(s.Title), Msg(s.Desc), Msg(s.Delta), Dir(s.Dir))).ToList(),
        report.Breakdown.Select(c => new InsightCategoryDto(c.Name, c.Icon, c.Amount.Amount, c.BarFraction, Dir(c.Dir))).ToList(),
        report.Trend.Select(t => new InsightTrendPointDto(t.Label, t.Outgoings.Amount, t.BarFraction, t.IsCurrent)).ToList(),
        report.MiniTrends.Select(mt => new InsightMiniTrendDto(
            Msg(mt.Label), mt.Icon, mt.Points, Msg(mt.CurrentText), Msg(mt.DeltaNote), Dir(mt.Dir))).ToList(),
        report.QuickWins.Select(w => Msg(w.Message)).ToList()));
});

accounts.MapPut("/{id:guid}/snapshot", async (Guid id, SaveAccountRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var version = await svc.SaveAsync(user.UserId(), id, req, ct);
    await notifier.AccountChangedAsync(id, user.UserId(), version);
    return Results.Ok(new AccountSnapshot(id, version, req.Payload));
});

// --- Member avatars (for showing profile pictures in member lists) -------
accounts.MapGet("/{id:guid}/avatars", async (Guid id, ClaimsPrincipal user, AvatarService avatars, CancellationToken ct) =>
    Results.Ok(await avatars.GetForAccountAsync(user.UserId(), id, ct)));

// --- Bank sync (Open Banking via GoCardless) -----------------------------
// Because linking a bank exposes REAL financial data (to the linker and to every member of a shared account),
// these endpoints require the caller's email to be verified — enforced server-side, not just in the UI. This is
// the legal backstop: an unverified user (or invited member) can never read real balances/transactions.
accounts.MapGet("/{id:guid}/bank/status", async (Guid id, ClaimsPrincipal user, BankSyncService svc, EmailVerificationService emailVerification, CancellationToken ct) =>
    await RequireVerifiedEmailAsync(user, emailVerification, ct)
        ?? Results.Ok(await svc.GetStatusAsync(user.UserId(), id, ct)));

accounts.MapGet("/{id:guid}/bank/institutions", async (Guid id, string? country, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.SearchInstitutionsAsync(user.UserId(), id, country ?? "GB", ct)));

accounts.MapPost("/{id:guid}/bank/link", async (Guid id, StartBankLinkRequest req, HttpContext http, ClaimsPrincipal user, BankSyncService svc, ConsentService consent, EmailVerificationService emailVerification, IConfiguration cfg, CancellationToken ct) =>
{
    if (await RequireVerifiedEmailAsync(user, emailVerification, ct) is { } notVerified) return notVerified;
    if (!await consent.IsActiveAsync(user.UserId(), id, ConsentService.Scope.BankLink, ct))
        return Results.Json(new { error = "Bank-link consent is required." }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(await svc.StartLinkAsync(user.UserId(), id, req, BankCallbackUrl(http, cfg), ct));
});

accounts.MapPost("/{id:guid}/bank/sync", async (Guid id, ClaimsPrincipal user, BankSyncService svc, EmailVerificationService emailVerification, CancellationToken ct) =>
{
    if (await RequireVerifiedEmailAsync(user, emailVerification, ct) is { } notVerified) return notVerified;
    await svc.SyncAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

accounts.MapGet("/{id:guid}/bank/pending", async (Guid id, ClaimsPrincipal user, BankSyncService svc, EmailVerificationService emailVerification, CancellationToken ct) =>
    await RequireVerifiedEmailAsync(user, emailVerification, ct)
        ?? Results.Ok(await svc.GetPendingAsync(user.UserId(), id, ct)));

accounts.MapGet("/{id:guid}/bank/accounts", async (Guid id, ClaimsPrincipal user, BankSyncService svc, EmailVerificationService emailVerification, CancellationToken ct) =>
    await RequireVerifiedEmailAsync(user, emailVerification, ct)
        ?? Results.Ok(await svc.ListAccountsAsync(user.UserId(), id, ct)));

// The recorded bank balance on or before a date (a closed period's end) — for showing month-end, not live.
accounts.MapGet("/{id:guid}/bank/balance-at", async (Guid id, DateOnly date, ClaimsPrincipal user, BankSyncService svc, EmailVerificationService emailVerification, CancellationToken ct) =>
    await RequireVerifiedEmailAsync(user, emailVerification, ct)
        ?? Results.Ok(new BankBalanceAtDto(await svc.BalanceAsOfAsync(user.UserId(), id, date, ct))));

accounts.MapPut("/{id:guid}/bank/account", async (Guid id, SelectBankAccountRequest req, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.SelectAccountAsync(user.UserId(), id, req.Ref, ct);
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/bank/ack", async (Guid id, BankTransactionAck ack, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.AckAsync(user.UserId(), id, ack.ExternalId, ack.Confirmed, ct);
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}/bank/connection", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.DisconnectAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

accounts.MapPost("/{id:guid}/bank/reset", async (Guid id, DateOnly from, DateOnly to, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.ResetRangeAsync(user.UserId(), id, from, to, ct);
    return Results.NoContent();
});

accounts.MapPut("/{id:guid}/bank/fund", async (Guid id, SetBankFundRequest req, ClaimsPrincipal user, BankSyncService svc, ConsentService consent, CancellationToken ct) =>
{
    // Binding a fund to the bank needs sync consent; unbinding (null) is always allowed.
    if (req.FundId is not null && !await consent.IsActiveAsync(user.UserId(), id, ConsentService.Scope.BankSync, ct))
        return Results.Json(new { error = "Fund-sync consent is required." }, statusCode: StatusCodes.Status403Forbidden);
    await svc.SetConnectionFundAsync(user.UserId(), id, req.FundId, ct);
    return Results.NoContent();
});

accounts.MapGet("/{id:guid}/bank/mappings", async (Guid id, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetMappingsAsync(user.UserId(), id, ct)));

accounts.MapPut("/{id:guid}/bank/mappings", async (Guid id, SetBankMappingRequest req, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.SetMappingAsync(user.UserId(), id, req.Description, req.Kind, req.TargetId, ct);
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}/bank/mappings", async (Guid id, string description, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.RemoveMappingAsync(user.UserId(), id, description, ct);
    return Results.NoContent();
});

// Public: the bank redirects here (with ?code=<auth code>&state=<accountId>) after the user consents. No auth —
// the code is exchanged with Enable Banking server-side to prove real consent — then we bounce to the SPA.
app.MapGet("/bank/callback", async (string? code, string? state, BankSyncService svc, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(code) && Guid.TryParseExact(state, "N", out var accountId)
        && await svc.CompleteLinkAsync(accountId, code, ct))
        return Results.Redirect("/?bank=linked");
    return Results.Redirect("/?bank=error");
});

// --- Excel export (one sheet per period) ---------------------------------
accounts.MapGet("/{id:guid}/export", async (Guid id, ClaimsPrincipal user, AccountExportService svc, CancellationToken ct) =>
{
    var (bytes, fileName) = await svc.ExportAsync(user.UserId(), id, ct);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

// --- Invitations ---------------------------------------------------------
accounts.MapPost("/{id:guid}/invitations", async (Guid id, CreateInvitationRequest req, ClaimsPrincipal user, InvitationService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var created = await svc.CreateAsync(user.UserId(), id, req.Username, ct);
    await notifier.InvitationReceivedAsync(created.InviteeUserId, created.InvitationId, created.AccountId, created.AccountName, created.InviterUsername);
    return Results.Ok();
}).RequireRateLimiting("invite");

var invitations = app.MapGroup("/invitations").RequireAuthorization();

invitations.MapGet("/pending", async (ClaimsPrincipal user, InvitationService svc, CancellationToken ct) =>
    Results.Ok(await svc.PendingForUserAsync(user.UserId(), ct)));

invitations.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, InvitationService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var accountId = await svc.AcceptAsync(user.UserId(), id, ct);
    await notifier.AccountChangedAsync(accountId, user.UserId());
    return Results.Ok(new { accountId });
});

invitations.MapPost("/{id:guid}/decline", async (Guid id, ClaimsPrincipal user, InvitationService svc, CancellationToken ct) =>
{
    await svc.DeclineAsync(user.UserId(), id, ct);
    return Results.NoContent();
});

app.MapHub<SyncHub>("/hubs/sync").RequireAuthorization();

// SPA fallback: any non-API route serves the WASM client's index.html (client-side routing).
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
});

app.Run();

// The provider redirect URI must exactly match what's registered in the Google/Facebook console.
// Behind a proxy (Cloud Run) the request scheme can read as http, so prefer an explicit Auth:PublicBaseUrl.
static string ExternalRedirectUri(HttpContext http, IConfiguration cfg, string provider)
{
    var baseUrl = cfg["Auth:PublicBaseUrl"]?.TrimEnd('/')
                  ?? $"{http.Request.Scheme}://{http.Request.Host}";
    return $"{baseUrl}/auth/external/{provider}/callback";
}

// Where the bank sends the user back after consent. Shares Auth:PublicBaseUrl so it's correct behind the
// Cloud Run proxy; this exact URL must be whitelisted for the app in the GoCardless dashboard.
static string BankCallbackUrl(HttpContext http, IConfiguration cfg)
{
    var baseUrl = cfg["Auth:PublicBaseUrl"]?.TrimEnd('/')
                  ?? $"{http.Request.Scheme}://{http.Request.Host}";
    return $"{baseUrl}/bank/callback";
}

// Public base URL the app is reached at, for building links in emails (verification, etc.).
static string AppBaseUrl(HttpContext http, IConfiguration cfg) =>
    (cfg["Email:AppBaseUrl"] ?? cfg["Auth:PublicBaseUrl"])?.TrimEnd('/')
    ?? $"{http.Request.Scheme}://{http.Request.Host}";

// Render an otpauth:// URI as a PNG QR image, returned as a data URL the client can drop straight into an <img>.
// PngByteQRCode is System.Drawing-free, so it works on Cloud Run's Linux base image.
static string QrDataUrl(string text)
{
    using var generator = new QRCoder.QRCodeGenerator();
    using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.Q);
    var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
    return "data:image/png;base64," + Convert.ToBase64String(png);
}

// Bank features expose real financial data, so they require a verified email. Returns a 403 result to
// short-circuit the endpoint with, or null when the caller's email is verified (proceed as normal).
static async Task<IResult?> RequireVerifiedEmailAsync(ClaimsPrincipal user, EmailVerificationService emailVerification, CancellationToken ct) =>
    await emailVerification.IsVerifiedAsync(user.UserId(), user.Email(), ct)
        ? null
        : Results.Json(new { error = "Please verify your email address to use bank features." }, statusCode: StatusCodes.Status403Forbidden);

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
