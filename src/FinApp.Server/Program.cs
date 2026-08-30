using System.Text;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Services;
using FinApp.Forecasting;
using FinApp.Persistence;
using FinApp.Server.Accounts;
using FinApp.Server.Assistant;
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
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<SignupService>();
builder.Services.AddSingleton<AdminPolicy>();          // owner allowlist (fails closed) for the P2 metrics
builder.Services.AddScoped<AdminMetricsService>();
builder.Services.AddSingleton<MonetizationService>();  // P4 rails: off by default, config flag flips it on
builder.Services.AddScoped<SubscriptionService>();     // who is on a paid plan — our record, not the provider's
builder.Services.AddSingleton<BetaPolicy>();           // free-beta seat cap + which addresses are ours
builder.Services.AddScoped<PlanOverrideService>();     // admin-only per-account plan pin, for testing the upgrade
builder.Services.AddScoped<EntitlementService>();      // single source of truth for plan resolution + server-side gating
builder.Services.AddSingleton<PaymentOptions>();
// The payment seam. Sandbox is the only implementation today and the default; adding a real one is a single
// registration change here plus a class implementing IPaymentProvider (see PaymentProvider.cs).
builder.Services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<PaymentOptions>().Provider switch
{
    _ => new SandboxPaymentProvider(),
});
builder.Services.AddScoped<ExternalAuthService>();
// The assistant (R3). Singletons because both hold process-lifetime state: the parser holds one API client, and
// the service holds the daily counters and the answer cache. Swapping IAssistantParser for an on-device or a fake
// implementation is the only change either the tests or R9 need.
builder.Services.AddSingleton<IAssistantParser, AnthropicAssistantParser>();
builder.Services.AddSingleton<AssistantService>();
builder.Services.AddScoped<AssistantUsageStore>();     // the spend counters; scoped because it rides the DbContext
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

// Gzip JSON responses. The payoff is GET /accounts/{id}/snapshot (~260KB of JSON, compresses ~8×), which the
// client now re-fetches after every command write (the Option-A cutover) — without this, each mutation costs a
// quarter-megabyte download. Cloud Run does not compress for us. HTTPS is fine here: BREACH needs a secret
// reflected next to attacker-controlled bytes in one response; snapshot responses carry only the user's own data.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json"];
});

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
    // Client error reports are anonymous by necessity (a crash can happen before sign-in), so they need their own
    // cap: generous enough that a genuinely broken page still gets through, tight enough that the endpoint can't
    // be used to flood our logs. Own bucket so a crash storm can't lock anyone out of signing in.
    options.AddPolicy("clienterrors", context => throttleAuth
        ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("dev"));
    // Feedback is anonymous and unauthenticated, so one person can sit and submit indefinitely. It used to share
    // the client-errors bucket (30/min), which is right for a crash storm and absurd for opinions — that allows
    // ~1,800 rows an hour from one address. Nothing they write can reach the landing page without an explicit
    // approval, so the risk is a flooded moderation queue rather than public spam; a tight own bucket keeps that
    // impractical while leaving genuine use (send it once, maybe fix a typo and resend) entirely unaffected.
    options.AddPolicy("feedback", context => throttleAuth
        ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("dev"));
    // The assistant is the one endpoint whose cost is somebody else's meter, so it gets the tightest burst bucket
    // in the app. ⚠️ Partitioned by USER, not by IP, unlike every policy above: this route is authenticated, a
    // household behind one address is the normal case here, and an IP key would let one member's questions
    // throttle their partner's. AssistantService owns the slower daily ceiling; this only stops a stuck client
    // from spending in a loop.
    options.AddPolicy("assistant", context => throttleAuth
        ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            // Same claim resolution as ClaimsPrincipalExtensions.UserId() — "sub" first, then NameIdentifier,
            // because whether the JWT handler maps one onto the other is a setting, not a guarantee.
            context.User.FindFirst("sub")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) })
        : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("dev"));
});

var app = builder.Build();

// Behind Cloud Run's TLS-terminating proxy the request reads as http; honour X-Forwarded-Proto so
// Request.Scheme is https (needed so the OAuth redirect_uri we build matches what providers expect).
// XForwardedFor is honoured too so Connection.RemoteIpAddress is the real client IP rather than the
// front-end proxy — that's what makes the per-IP rate limits (auth/invite/clienterrors/feedback) actually
// per-client on Cloud Run instead of a single shared bucket keyed on the proxy. ForwardLimit=1 takes only
// the entry the trusted front end appended, so a client-supplied X-Forwarded-For can't spoof the key; if the
// header shape ever differs the key just falls back to the proxy address (the prior behaviour) — never worse.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    ForwardLimit = 1,
};
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// ⛔ Why this whole block is wrapped in a retry (S101).
//
// Every line below runs BEFORE the port is open, so anything that throws here doesn't degrade the app — it aborts
// the process, the startup probe fails, and Cloud Run has no container. That happened on the finapp-00297-w9p
// deploy: both starting instances died with
//     Npgsql.NpgsqlException → TimeoutException: Timeout during reading attempt
//       … AuthenticateSASL … at DatabaseFacade.EnsureCreated() at Program.<Main>$:245
// i.e. the managed Postgres simply took too long to finish authenticating a cold connection. Neon scales to zero,
// so a slow first connect is ORDINARY, not exceptional — and a transient database hiccup must not be the same
// thing as "this build cannot boot".
//
// The retry makes a slow database DELAY readiness instead of killing the process. Deliberately:
//   • the whole block, not just EnsureCreated — every EnsureSchemaAsync below opens the same cold connection, so
//     guarding one line just moves the crash down a few rows;
//   • idempotent by construction (EnsureCreated / CREATE TABLE IF NOT EXISTS / Migrate), so re-running a partly
//     completed pass is safe — that is what makes a retry legitimate here rather than a gamble;
//   • it still THROWS after the last attempt. A server that came up without its schema would fail every request
//     with something far less obvious than a boot failure, so a genuinely broken database should still be loud.
static async Task WithDbRetryAsync(ILogger logger, string what, Func<Task> action)
{
    const int attempts = 5;
    for (var attempt = 1; ; attempt++)
    {
        try { await action(); return; }
        catch (Exception ex) when (attempt < attempts)
        {
            // 1s, 2s, 4s, 8s — about 15s of patience in total, comfortably inside Cloud Run's startup grace and
            // well past a Neon cold start, without holding a genuinely dead deploy up for minutes.
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            logger.LogWarning(ex, "{What} failed on attempt {Attempt}/{Attempts}; retrying in {Delay}s.",
                what, attempt, attempts, delay.TotalSeconds);
            await Task.Delay(delay);
        }
    }
}

// Ensure the server DB schema is current on startup.
// SQLite uses the EF migrations; Postgres uses EnsureCreated (the migrations are SQLite-specific,
// and the cloud DB is provisioned fresh) so we build the schema straight from the model.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FinAppDbContext>();
    await WithDbRetryAsync(app.Logger, "Database schema check", () =>
    {
        if (usePostgres) db.Database.EnsureCreated();
        else db.Database.Migrate();
        return Task.CompletedTask;
    });
    // Every one of these is an idempotent CREATE TABLE IF NOT EXISTS, so the whole run is safe to repeat — which is
    // what lets one retry cover the lot rather than needing one per service.
    var archives = scope.ServiceProvider.GetRequiredService<ArchivedAccountsService>();
    var deletions = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
    await WithDbRetryAsync(app.Logger, "Auxiliary table setup", async () =>
    {
        // Avatars live in a standalone table created idempotently (no EF migration; works on both providers).
        await scope.ServiceProvider.GetRequiredService<AvatarService>().EnsureSchemaAsync();
        // Bank-sync tables (connections + staged transactions) follow the same idempotent-create pattern.
        await scope.ServiceProvider.GetRequiredService<BankSyncService>().EnsureSchemaAsync();
        // External-identity marker table (which users signed up via Google/Facebook) — same pattern.
        await scope.ServiceProvider.GetRequiredService<ExternalIdentityService>().EnsureSchemaAsync();
        // Consent audit log (login / bank-link / bank-sync grants + withdrawals).
        await scope.ServiceProvider.GetRequiredService<ConsentService>().EnsureSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<FeedbackService>().EnsureSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<SignupService>().EnsureSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<SubscriptionService>().EnsureSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<PlanOverrideService>().EnsureSchemaAsync();
        // The assistant's per-user spend counters (R3). Durable and shared on purpose — see the type's own note.
        await scope.ServiceProvider.GetRequiredService<AssistantUsageStore>().EnsureSchemaAsync();
        // Refresh-token store (rotation + reuse detection) — same idempotent-create pattern.
        await scope.ServiceProvider.GetRequiredService<RefreshTokenService>().EnsureSchemaAsync();
        // One-time auth codes for external sign-in (keeps session tokens out of the redirect URL).
        await scope.ServiceProvider.GetRequiredService<AuthCodeService>().EnsureSchemaAsync();
        // Email-verification state + one-time confirmation tokens.
        await scope.ServiceProvider.GetRequiredService<EmailVerificationService>().EnsureSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<PasswordResetService>().EnsureSchemaAsync();
        // Two-factor (TOTP) secrets + recovery codes.
        await scope.ServiceProvider.GetRequiredService<TwoFactorService>().EnsureSchemaAsync();
        // Archived-accounts table (its purge runs below, outside the retry — housekeeping, not schema).
        await archives.EnsureSchemaAsync();
        // Pending user-deletion table (same).
        await deletions.EnsureSchemaAsync();
    });
    // Both purges are HOUSEKEEPING and must never stop the app serving: this runs before the port is open, so
    // an exception here is a container that fails its startup probe. (It has happened: a multi-instance deploy
    // raced the archived-account purge into a DbUpdateConcurrencyException and the process aborted.) Each
    // service already swallows per-row failures; this is the belt to that pair of braces.
    try
    {
        var purgedAccounts = await archives.PurgeExpiredAsync();
        if (purgedAccounts > 0) app.Logger.LogInformation("Purged {Count} archived account(s) past the grace window.", purgedAccounts);
        var purgedUsers = await deletions.PurgeDueAsync();
        if (purgedUsers > 0) app.Logger.LogInformation("Purged {Count} pending user deletion(s) past the grace window.", purgedUsers);
    }
    catch (Exception ex) { app.Logger.LogError(ex, "Startup retention purge failed; leaving it for the next start."); }
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
    // ⚠️ `font-src 'self'` and `style-src` without a CDN are LOAD-BEARING, not leftovers. Until 2026-08-30
    // index.html carried a fonts.googleapis.com stylesheet that this policy refused on every single page
    // load, and the whole app rendered in the system fallback in production for as long as that header has
    // existed — a violation nobody read because the page still looked plausible. The font is now self-hosted
    // (css/fonts/*.woff2, @font-face at the top of app.css). If a web font is ever needed again, ship the
    // bytes from this origin; do NOT re-open this policy to a font CDN. Self-hosting also keeps the client
    // free of cross-origin requests entirely, which is the same privacy line the inlined avatars took.
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
            // A 402 also names the blocked feature so the client can raise the matching upgrade prompt — identical
            // UX whether the gate fired locally or the server refused.
            if (ex is PaymentRequiredException pr)
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, feature = pr.FeatureKey });
            else
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

// Gzip JSON responses for clients that ask (Accept-Encoding) — see AddResponseCompression above.
app.UseResponseCompression();

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
    var oauthCookie = new CookieOptions
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/",
    };
    http.Response.Cookies.Append("finapp_oauth_state", state, oauthCookie);
    // A native (mobile app) start (?native=1) is remembered so the callback redirects the result back into the
    // app via the com.tandemtab.app:// deep link instead of the web SPA. Web callers never send this.
    if (http.Request.Query["native"] == "1")
        http.Response.Cookies.Append("finapp_oauth_native", "1", oauthCookie);
    return Results.Redirect(ext.BuildAuthorizeUrl(provider, redirectUri, state));
});

auth.MapGet("/external/{provider}/callback", async (string provider, string? code, string? state,
    HttpContext http, ExternalAuthService ext, AuthService authSvc, AuthCodeService authCodes,
    AvatarService avatars, ExternalIdentityService identities, IConfiguration cfg, ILoggerFactory logs,
    CancellationToken ct) =>
{
    // ⚠️ Named "FinApp.ExternalAuth" so it is greppable in Cloud Logging (textPayload:"FinApp.ExternalAuth").
    // Every exit from this handler used to be silent, and the cost of that was real: when Google sign-in broke
    // in production the only trace anywhere was an ABSENCE — no callback in the request log — which you can
    // only notice if you already suspect it. A one-time redirect endpoint that can fail five different ways
    // has to say which one.
    var log = logs.CreateLogger("FinApp.ExternalAuth");
    // Was this flow started from the native app? Route the outcome back into the app via its deep link.
    var native = http.Request.Cookies["finapp_oauth_native"] == "1";
    http.Response.Cookies.Delete("finapp_oauth_native");
    string Fail(string why)
    {
        log.LogWarning("External sign-in ({Provider}) failed: {Why}.", provider, why);
        return native ? "com.tandemtab.app://auth/callback?error=1" : "/?authError=1";
    }
    string Ok(string authCode) => native
        ? $"com.tandemtab.app://auth/callback?authCode={Uri.EscapeDataString(authCode)}"
        : $"/?authCode={Uri.EscapeDataString(authCode)}";

    if (!ext.IsEnabled(provider)) return Results.Redirect(Fail("the provider is not configured"));
    if (string.IsNullOrEmpty(code)) return Results.Redirect(Fail("the provider returned no authorization code"));
    var expectedState = http.Request.Cookies["finapp_oauth_state"];
    http.Response.Cookies.Delete("finapp_oauth_state");
    // Distinguished deliberately: a MISSING cookie is the browser not sending it back (SameSite, a stripped
    // cookie, a flow older than the 10-minute leash), while a MISMATCH is the state not being ours at all.
    // Collapsing the two hid which of them was happening for as long as this was one silent branch.
    if (string.IsNullOrEmpty(state)) return Results.Redirect(Fail("the provider returned no state"));
    if (string.IsNullOrEmpty(expectedState)) return Results.Redirect(Fail("the state cookie was not sent back"));
    if (state != expectedState) return Results.Redirect(Fail("the state did not match the cookie"));
    try
    {
        var redirectUri = ExternalRedirectUri(http, cfg, provider);
        var (email, name, picture) = await ext.CompleteAsync(provider, code, redirectUri, ct);
        var userId = await authSvc.FindOrCreateExternalUserAsync(email, name, ct);
        await identities.MarkAsync(userId, provider, ct);   // so the UI can hide "change password" for them
        // Adopt the provider's profile picture only if the user hasn't set one of their own — and adopt it INLINE,
        // never as the provider's URL (see ExternalAuthService.FetchInlineAvatarAsync for why: a stored
        // googleusercontent URL renders in a browser and nowhere else, so the phone showed no picture at all).
        // ⚠️ The `!IsInline` test is also the backfill: everyone who signed in before this has a remote URL stored,
        // and it is replaced on their next sign-in. An avatar the user uploaded themselves is already inline, so
        // this never overwrites one. A failed download changes nothing — the old value stands.
        var storedAvatar = await avatars.GetAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(picture) && !AvatarService.IsInline(storedAvatar)
            && await ext.FetchInlineAvatarAsync(picture, ct) is { } inlineAvatar)
            await avatars.SetAsync(userId, inlineAvatar, ct);
        // Hand the caller a one-time code (not a token) in the query string. The client POSTs it to /auth/exchange
        // for the real access + refresh token, keeping session tokens out of the URL/history/Referer.
        var authCode = await authCodes.IssueAsync(userId, ct);
        log.LogInformation("External sign-in ({Provider}) completed; handing back a one-time code.", provider);
        return Results.Redirect(Ok(authCode));
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "External sign-in ({Provider}) threw during the token exchange or user lookup.", provider);
        return Results.Redirect(Fail("an exception during completion"));
    }
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

// --- Client error reports (OPEN-BETA B1) ---------------------------------------------------------------------
// The client has no other way to tell us it broke: a WASM exception goes to that user's console and nowhere else.
// BUG-1 (sign-out crashing the app) sat unnoticed for five days for exactly this reason.
//
// Deliberately ANONYMOUS: a crash can happen on the landing page, during registration, or *because* auth is
// broken — the reports we most need are the ones a signed-in-only endpoint would drop. The cost of that is an
// open write path, so it's rate-limited on its own bucket and every field is re-scrubbed here regardless of what
// the client claims to have done.
//
// Logged, not stored: it goes to ILogger as structured fields and lands in Cloud Logging, which we already query
// when verifying deploys. No table, no migration, no third-party processor to declare in the privacy policy.
app.MapPost("/client-errors", (ClientErrorReport report, ILoggerFactory logs, ClaimsPrincipal? user) =>
{
    var clean = ErrorScrubber.Clean(report);
    if (clean.Message.Length == 0) return Results.NoContent();   // nothing to say; don't log an empty row

    // Named so it can be isolated in Cloud Logging. We log via the default text console, so the entry lands in
    // textPayload (not jsonPayload) — match on the substring:
    //   gcloud logging read 'textPayload:"FinApp.ClientError"' --limit 50 --freshness=1d
    logs.CreateLogger("FinApp.ClientError").LogError(
        "Client error [{Kind}] at {Where}: {ClientMessage} | app={AppVersion} ua={UserAgent} user={UserId}\n{ClientStack}",
        clean.Kind, clean.Where ?? "?", clean.Message, clean.AppVersion ?? "?", clean.UserAgent ?? "?",
        // The user id correlates repeat failures without naming anyone; it's absent on an anonymous crash.
        user?.Identity?.IsAuthenticated == true ? user.UserId().ToString() : "anon",
        clean.Stack ?? "");

    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("clienterrors");

// --- User feedback (OPEN-BETA B2) ----------------------------------------------------------------------------
// B1 catches crashes; this catches everything that isn't a crash — confusing, slow, didn't trust it. Testers who
// can't report don't report, they churn.
//
// ANONYMOUS on purpose, and not only for convenience: the landing page is where someone who looked at the
// product and decided NOT to sign up can tell us why, which is feedback we can get no other way.
//
// Stored, not just logged (unlike client errors): we want to come back to it, and a review can only be quoted
// publicly if that person ticked the box for that review, which needs a persisted consent flag. Also logged, so
// it shows up beside the errors in Cloud Logging where we're already looking.
app.MapPost("/feedback", async (FeedbackRequest req, FeedbackService feedback, ILoggerFactory logs,
        ClaimsPrincipal? user, CancellationToken ct) =>
{
    var rating = req.Rating is { } r && r is >= 1 and <= 5 ? r : (int?)null;
    var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
    if (rating is null && comment is null) return Results.NoContent();   // nothing said; don't store an empty row

    var userId = user?.Identity?.IsAuthenticated == true ? user.UserId() : (Guid?)null;
    await feedback.RecordAsync(userId, rating, comment, req.PublicConsent,
        req.Source, req.AppVersion, req.UserAgent, ct);

    //   gcloud logging read 'textPayload:"FinApp.Feedback"' --limit 50 --freshness=7d  (text console → textPayload)
    logs.CreateLogger("FinApp.Feedback").LogInformation(
        "Feedback {Rating}/5 from {Source} (user={UserId}, public={PublicConsent}): {Comment}",
        rating?.ToString() ?? "-", req.Source, userId?.ToString() ?? "anon", req.PublicConsent, comment ?? "");

    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("feedback");

app.MapGet("/me", async (ClaimsPrincipal user, AvatarService avatars, ExternalIdentityService identities,
        EmailVerificationService emailVerification, TwoFactorService twoFactor, AccountDeletionService deletions,
        AdminPolicy adminPolicy, EntitlementService entitlements, CancellationToken ct) =>
{
    // Plan resolution (override → flag → cohort/subscription) lives in one place now, so /me, /plans and every
    // gated endpoint can't drift. A pinned override implies monetization is live FOR THIS ACCOUNT ONLY.
    var ent = await entitlements.ResolveAsync(user.UserId(), ct);
    return Results.Ok(new UserDto(user.UserId(), user.Username(), user.Email(),
        await avatars.GetAsync(user.UserId(), ct), await identities.GetProviderAsync(user.UserId(), ct),
        EmailVerified: await emailVerification.IsVerifiedAsync(user.UserId(), user.Email(), ct),
        TwoFactorEnabled: await twoFactor.IsEnabledAsync(user.UserId(), ct),
        PendingDeletionAt: await deletions.ScheduledAtAsync(user.UserId(), ct),
        IsAdmin: adminPolicy.IsAdmin(user.Email()),
        MonetizationEnabled: ent.MonetizationLive,
        Plan: ent.Plan,
        // The Pro tag + logo crown. During beta (flag off) it follows the cohort so the first-N lifetime members
        // are badged Pro while everyone stays ungated; when monetization is live it's a strict Pro plan.
        ProBadge: EntitlementService.ShowsProBadge(ent)));
}).RequireAuthorization();

// The Plans screen's data (OPEN-BETA P4). Only meaningful when the flag is on; while off it reports Enabled=false
// and "unlimited", so the client shows no plan UI at all. Prices are config values, never hard-coded.
app.MapGet("/plans", async (ClaimsPrincipal user, MonetizationService monetization,
        EntitlementService entitlements, PaymentOptions payments, CancellationToken ct) =>
{
    // Same resolver as /me. GrandfatheredBeta is already "beta-cohort AND actually on Pro", so a tester pinned to
    // Free never sees "Pro is on us" over a Free plan.
    var ent = await entitlements.ResolveAsync(user.UserId(), ct);
    return Results.Ok(new PlansDto(ent.MonetizationLive, ent.Plan, ent.GrandfatheredBeta,
        monetization.Currency, monetization.AnnualPrice, monetization.MonthlyPrice,
        MonetizationService.Catalogue, payments.Provider, payments.Sandbox));
}).RequireAuthorization();

// Free-beta capacity for the landing page. Anonymous: a stranger deciding whether to sign up is exactly who
// needs to see how many seats are left.
app.MapGet("/beta/capacity", async (BetaPolicy beta, SignupService signups, CancellationToken ct) =>
{
    var taken = beta.Enabled ? await signups.BetaSeatsTakenAsync(beta.CountFrom, ct) : 0;
    return Results.Ok(new BetaCapacityDto(beta.Enabled, beta.Cap, taken,
        beta.Enabled ? beta.Remaining(taken) : null, beta.IsFull(taken)));
}).AllowAnonymous();

// Admin-only: pin the CALLING admin's own account to a plan, so the Free → upgrade → Pro journey can be walked
// on demand while wiring a payment provider. Deliberately self-only — an endpoint that could re-plan an
// arbitrary user is a far bigger blast radius than this needs, and the owner only ever wants to test on
// themselves. Clearing it returns the account to the normal rules.
app.MapPost("/admin/plan-override", async (PlanOverrideRequest req, ClaimsPrincipal user,
        AdminPolicy adminPolicy, PlanOverrideService overrides, CancellationToken ct) =>
{
    if (!adminPolicy.IsAdmin(user.Email())) return Results.Forbid();
    await overrides.SetAsync(user.UserId(), req.Plan, ct);
    return Results.NoContent();
}).RequireAuthorization();

// Admin-only: re-classify an account's cohort, by email.
// The safety net behind BetaPolicy's pattern list. Patterns catch a test address at sign-up, but they can't catch
// an OAuth sign-in (the provider hands over the real address, so no +test alias is possible) or an account made
// before the patterns were configured. Those land in the beta cohort holding a lifetime-Pro seat, and previously
// the only fix was raw SQL against prod. Accepts only the three known cohorts so a typo can't invent a fourth
// that every downstream check would then treat as "not beta".
app.MapPost("/admin/cohort", async (SetCohortRequest req, ClaimsPrincipal user, AdminPolicy adminPolicy,
        FinAppDbContext db, SignupService signups, ILoggerFactory logs, CancellationToken ct) =>
{
    if (!adminPolicy.IsAdmin(user.Email())) return Results.Forbid();

    var cohort = (req.Cohort ?? "").Trim().ToLowerInvariant();
    if (cohort is not (SignupService.BetaCohort or SignupService.FreeCohort or SignupService.TestCohort))
        return Results.BadRequest(new { error = "Cohort must be beta, free or test." });

    var email = (req.Email ?? "").Trim().ToLowerInvariant();
    var target = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
    if (target is null) return Results.NotFound(new { error = "No account with that email." });

    await signups.SetCohortAsync(target.Id, cohort, ct);
    // Logged because this hands out (or takes away) a lifetime entitlement — worth an audit trail.
    logs.CreateLogger("FinApp.Admin").LogInformation(
        "Cohort for {Email} set to {Cohort} by {Admin}", email, cohort, user.Email());
    return Results.Ok(new CohortResultDto(email, cohort,
        CountsAsBetaMember: cohort == SignupService.BetaCohort));
}).RequireAuthorization();

// Who is in a cohort. Split from /admin/metrics on purpose: that endpoint is a counts-only view of the beta and
// stays one, so a name is only ever read when an admin asks for a specific cohort — which they do to find the
// email that POST /admin/cohort needs. Identity + join date only; no account snapshot is opened.
app.MapGet("/admin/cohort/{cohort}/members", async (string cohort, ClaimsPrincipal user, AdminPolicy adminPolicy,
        AdminMetricsService metrics, CancellationToken ct) =>
    adminPolicy.IsAdmin(user.Email())
        ? Results.Ok(await metrics.MembersAsync(cohort, ct: ct))
        : Results.Forbid()).RequireAuthorization();

// Admin-only review moderation. The approval gate shipped without a door — the column defaulted to 0 and
// nothing could ever set it, so the landing carousel could never fill. This is that door.
app.MapGet("/admin/feedback", async (ClaimsPrincipal user, AdminPolicy adminPolicy, FeedbackService feedback,
        CancellationToken ct) =>
    adminPolicy.IsAdmin(user.Email())
        ? Results.Ok((await feedback.ModerationQueueAsync(50, ct))
            .Select(f => new AdminFeedbackDto(f.Id, f.Rating, f.Comment, f.Consent, f.Approved, f.Source, f.At)).ToList())
        : Results.Forbid())
    .RequireAuthorization();

app.MapPost("/admin/feedback/{id}/approve", async (string id, ApproveReviewRequest req, ClaimsPrincipal user,
        AdminPolicy adminPolicy, FeedbackService feedback, CancellationToken ct) =>
{
    if (!adminPolicy.IsAdmin(user.Email())) return Results.Forbid();
    await feedback.SetApprovedAsync(id, req.Approved, ct);
    return Results.NoContent();
}).RequireAuthorization();

// The public pricing shown on the landing page, BEFORE anyone signs in — the plan choice moved there so the
// price is visible while deciding, not discovered after registering. Anonymous by design and deliberately narrow:
// prices and the tier table only, no per-user plan (there is no user yet). Returns Enabled=false during beta, and
// the landing page renders no pricing at all in that case.
app.MapGet("/plans/public", (MonetizationService monetization) =>
    Results.Ok(new PlansDto(monetization.Enabled, "free", false, monetization.Currency,
        monetization.AnnualPrice, monetization.MonthlyPrice, MonetizationService.Catalogue, "", true)))
    .AllowAnonymous();

// Consented AND moderator-approved reviews for the landing carousel (OPEN-BETA P1). Both gates are enforced in
// the query — see FeedbackService.PublicReviewsAsync for why consent alone would be unsafe on an endpoint whose
// write side is anonymous.
app.MapGet("/reviews/public", async (FeedbackService feedback, CancellationToken ct) =>
    Results.Ok((await feedback.PublicReviewsAsync(12, ct))
        .Select(r => new PublicReviewDto(r.Rating, r.Comment, r.At)).ToList()))
    .AllowAnonymous();

// Start an upgrade (OPEN-BETA P4 / payment-provider prep).
// Gated on the CALLER'S resolved entitlement, not the raw global flag — the same value /me reports as
// MonetizationEnabled. That matters because an admin plan pin deliberately makes monetization live for that one
// account: checking the flag here instead meant the client showed "Upgrade to Pro" (it trusts /me) and the
// endpoint then 404'd, so the test switch could never actually rehearse a checkout. Ordinary accounts during beta
// still resolve to MonetizationLive=false, so the rails stay unreachable for them.
app.MapPost("/billing/checkout", async (CheckoutRequest req, ClaimsPrincipal user, HttpContext http,
        EntitlementService entitlements, IPaymentProvider payments, CancellationToken ct) =>
{
    if (!(await entitlements.ResolveAsync(user.UserId(), ct)).MonetizationLive) return Results.NotFound();
    var origin = $"{http.Request.Scheme}://{http.Request.Host}";
    return Results.Ok(await payments.CreateCheckoutAsync(user.UserId(), req.Interval, origin, ct));
}).RequireAuthorization();

// Completes a SANDBOX checkout — the stand-in for a provider webhook. Refuses to run unless the active provider
// actually is the sandbox, so this can never become a free-Pro button once a real provider is configured.
app.MapPost("/billing/sandbox/complete", async (CheckoutRequest req, ClaimsPrincipal user,
        EntitlementService entitlements, IPaymentProvider payments, SubscriptionService subscriptions,
        PlanOverrideService overrides, ILoggerFactory logs, CancellationToken ct) =>
{
    if (!(await entitlements.ResolveAsync(user.UserId(), ct)).MonetizationLive || !payments.IsSandbox)
        return Results.NotFound();
    var expires = MonetizationService.ExpiryFor(req.Interval, DateTimeOffset.UtcNow);
    await subscriptions.ActivateAsync(user.UserId(), req.Interval.ToString(), payments.Name, null, true, expires, ct);
    // Land the tester on Pro, the state a real upgrade produces.
    // Two things force this rather than simply clearing the pin. A pin outranks the subscription in
    // EntitlementService, so leaving it as "free" would make a completed upgrade change nothing on screen. But
    // CLEARING it is no better while the global flag is off: resolution then short-circuits before it ever looks
    // at subscriptions, and the account falls back to its cohort default ("unlimited" for a beta member, "free"
    // for everyone else) — so the purchase would still be invisible. Pinning "pro" is the only way a completed
    // sandbox purchase is reflected pre-launch. "Exit test mode" in the admin console clears it.
    await overrides.SetAsync(user.UserId(), "pro", ct);
    logs.CreateLogger("FinApp.Billing").LogInformation(
        "Sandbox subscription activated for {UserId} ({Interval}, expires {Expires:o})",
        user.UserId(), req.Interval, expires);
    return Results.NoContent();
}).RequireAuthorization();

// Owner-only usage metrics (OPEN-BETA P2). Server-side authorization — an endpoint that enumerates users is the
// highest-value target in the app, so the role check lives here, not behind a hidden route. Returns counts and
// timestamps only; never any other person's financial data.
app.MapGet("/admin/metrics", async (ClaimsPrincipal user, AdminPolicy adminPolicy, AdminMetricsService metrics, CancellationToken ct) =>
    adminPolicy.IsAdmin(user.Email())
        ? Results.Ok(await metrics.BuildAsync(ct))
        : Results.Forbid())
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

accounts.MapPost("", async (CreateAccountRequest req, ClaimsPrincipal user, AccountService svc,
        EntitlementService entitlements, CancellationToken ct) =>
{
    // Free = 1 account (MONETIZATION.md). The 2nd+ needs the "caps" entitlement; inert for unlimited/pro, so this
    // changes nothing until monetization is live for the account.
    if (await svc.OwnedCountAsync(user.UserId(), ct) >= 1)
        await entitlements.RequireAsync(user.UserId(), PlanFeatures.Caps, ct);
    return Results.Ok(await svc.CreateAsync(user.UserId(), user.Username(), req, ct));
});

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
accounts.MapGet("/{id:guid}/overview", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(AccountOverviewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    if ((ResolvePeriod(account, period) ?? account.CurrentPeriod) is not { } target) return Results.Ok(AccountOverviewDto.Empty with { Currency = account.Currency });
    // Overlay the live bank balance so the thin header matches the thick app (see SpendingMap.Overview).
    var bank = await bankSvc.GetStatusAsync(user.UserId(), id, ct);
    return Results.Ok(SpendingMap.Overview(account, target, bank.Balance, bank.BalanceCurrency));
});

// Path-B thin-Spending read: the whole surface (expenses + overview + picker options) in one call, so a thin
// client renders the tab with no snapshot and no domain. Paired with the delta-returning expense writes above.
accounts.MapGet("/{id:guid}/spending", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(SpendingViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var bank = await bankSvc.GetStatusAsync(user.UserId(), id, ct);
    return Results.Ok(SpendingMap.View(account, snap.Version, bank.Balance, bank.BalanceCurrency, ResolvePeriod(account, period)));
});

// Path-B thin-Wallets read: funds + balances + this period's transfers in one call. Paired with the fund writes
// below, which return a FundMutationDto carrying a refreshed view so the client reconciles with no re-fetch.
accounts.MapGet("/{id:guid}/wallets", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, AccountService accountSvc, BankSyncService bankSvc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(WalletsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var bank = await bankSvc.GetStatusAsync(user.UserId(), id, ct);
    // The aggregate knows the id it sent money to, never the name — that lives on the caller's other accounts.
    var names = (await accountSvc.ListForUserAsync(user.UserId(), ct)).ToDictionary(a => a.Id, a => a.Name);
    return Results.Ok(WalletsMap.View(account, snap.Version, bank.Balance, bank.BalanceCurrency, ResolvePeriod(account, period), names));
});

// Path-B thin-Goals read: every bucket with its computed figures (goal progress / debt payoff / investment
// projection / sinking set-aside), the free-to-save cap, and this period's deposits. Paired with the savings-deposit
// write below (returns a SavingsMutationDto carrying a refreshed view).
accounts.MapGet("/{id:guid}/savings", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(SavingsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var bank = await bankSvc.GetStatusAsync(user.UserId(), id, ct);
    return Results.Ok(SavingsMap.View(account, snap.Version, bank.Balance, bank.BalanceCurrency, ResolvePeriod(account, period)));
});

// The Breakdown ring + the four figures beside it. ★ Until now NO server read stood behind this chart at all, so
// every attempt to size it as client work was sizing the wrong half — the rules are the expensive part and they
// live in BreakdownMap. `from`/`to` default to the viewed period; `groupBy` is category (default), tag or fund.
accounts.MapGet("/{id:guid}/breakdown", async (Guid id, int? period, DateOnly? from, DateOnly? to, string? groupBy,
        ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(BreakdownViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(BreakdownMap.View(account, ResolvePeriod(account, period), from, to, groupBy));
});

// The debt-payoff forecast for one bucket. ★ Its own read rather than a fatter /savings: it is only ever wanted
// for the ONE debt somebody has opened, it runs an amortisation per call, and folding it into the list read would
// make every Goals render pay for a schedule nobody is looking at.
// ⚠️ Gated INSIDE the map, not at the door: the schedule and the lump-sum figures are Free (the web shows those
// too), and only the modelling of the bank's alternatives is withheld. A 402 here would withhold the whole screen.
accounts.MapGet("/{id:guid}/savings/{bucketId:guid}/payoff", async (Guid id, Guid bucketId, int? period,
        ClaimsPrincipal user, SnapshotService svc, EntitlementService entitlements, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(DebtPayoffDto.None);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var proDebt = await entitlements.AllowsAsync(user.UserId(), PlanFeatures.Debt, ct);
    return Results.Ok(SavingsMap.Payoff(account, bucketId, proDebt, ResolvePeriod(account, period)));
});

// The whole-stack payoff plan: every debt at once, under avalanche or snowball, with one shared extra per month.
// ★ Distinct from /savings/{bucketId}/payoff above, which answers "when does THIS loan end". Until this existed a
// thin client could not answer "when am I debt-free" at all — the web computed it in the thick client, which is
// exactly the kind of number a second implementation gets plausibly wrong.
// ⚠️ Ungated on purpose: the web's planner card is free, and the debt-free date is on its Home card for everyone.
accounts.MapGet("/{id:guid}/savings/plan", async (Guid id, decimal? extra, string? strategy, int? period,
        ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(DebtPlanDto.None);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(SavingsMap.Plan(account, extra ?? 0m, strategy, ResolvePeriod(account, period)));
});

// Trends: one row per period — money in, spent, kept, set aside, the closing balance and debt repaid.
// ★ The largest read of the R2.5 slice, and a SERVER row rather than a client one: no other thin contract carries
// a per-period total, so without this a client would have to fetch every period's surface read and re-add them.
// `from`/`to` select whole periods that overlap the window; omitting BOTH means all time (see TrendsViewDto).
// `focus`/`focusId` narrow the second series to one category or one bucket (the web's O14 picker).
accounts.MapGet("/{id:guid}/trends", async (Guid id, DateOnly? from, DateOnly? to, string? focus, Guid? focusId,
        ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(TrendsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(TrendsMap.View(account, from, to, focus, focusId ?? Guid.Empty));
});

// The week recap: "your week in money" for the last completed Monday–Sunday. The third and last of R2.5's
// server-read rows, and a server row for the same reason as Trends — WeeklyRecapService walks every period's
// expenses, savings and contributions, and no thin contract carries a week-shaped total. The week is not a slice
// of a period either: it straddles two of them for roughly a quarter of the weeks in a year.
// `today` is the caller's own local date, as on /active-trips: which week counts as "last completed" is a
// question about the reader's day, and a server in UTC flips it a day early or late for half the world.
accounts.MapGet("/{id:guid}/week-recap", async (Guid id, DateOnly? today,
        ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(WeeklyRecapViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(RecapMap.View(account, today ?? DateOnly.FromDateTime(DateTime.UtcNow)));
});

// Path-B thin-Budgets read: every budgeted category with its coverage. Paired with the budget writes (delta below).
accounts.MapGet("/{id:guid}/budgets", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(BudgetsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(BudgetsMap.View(account, snap.Version, ResolvePeriod(account, period)));
});

// Search every period's expenses — what a thin client's "find an older expense" pickers need. ★ Every other
// spending read is period-scoped, and the charge a refund belongs to is routinely two months back (owner report,
// S119). `refundableOnly` keeps rows that still carry money; `q` matches note, category and amount.
accounts.MapGet("/{id:guid}/expenses/search", async (Guid id, string? q, int? take, bool? refundableOnly,
        ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(ExpenseSearchDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(ExpenseSearchMap.View(account, q, take ?? 60, refundableOnly ?? false));
});

// Path-B faster-expense-entry read: recent manual expenses the add-expense modal derives its chips/suggestions from
// (account-level — spans all periods, not period-scoped).
accounts.MapGet("/{id:guid}/expense-entry", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(ExpenseEntryDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(ExpenseEntryMap.View(account, snap.Version));
});

// Path-B thin-Recurring read: bills/income expectations with their due state for the open period.
accounts.MapGet("/{id:guid}/recurring", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(RecurringViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(RecurringView.Of(account, snap.Version, ResolvePeriod(account, period)));
});

// Path-B thin-Income read: this period's deposits + the contribution-category/fund pickers + overview. Paired
// with the delta-returning deposit writes (POST/PUT/DELETE /deposits) below.
accounts.MapGet("/{id:guid}/income", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(IncomeViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var bank = await bankSvc.GetStatusAsync(user.UserId(), id, ct);
    return Results.Ok(IncomeMap.View(account, snap.Version, bank.Balance, bank.BalanceCurrency, ResolvePeriod(account, period)));
});

// Path-B thin Account settings: the editable per-account settings (name, currency, savings-rate target). Name is
// changed via PUT /{id}/name (relational); the savings target rides the mutation spine (PUT /{id}/savings-target).
accounts.MapGet("/{id:guid}/settings", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(AccountSettingsDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(new AccountSettingsDto(account.Name, account.Currency, account.SavingsRateTarget));
});

// Path-B thin account structure: the editable categories, funds and contribution categories (for the thin
// structure editor). Account-level; the create/edit/archive/remove commands live below (Session 44).
accounts.MapGet("/{id:guid}/structure", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(StructureViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(StructureMap.View(account, snap.Version));
});

accounts.MapPut("/{id:guid}/savings-target", async (Guid id, SetSavingsTargetRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync(userId, id, account =>
    {
        account.SetSavingsRateTarget(req.Percent / 100m);   // domain validates 0..1 (else 400)
        return 0;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, null));
});

// Path-B thin period navigation: the account's periods (oldest→newest) so the thin client can render prev/next
// month nav and know which one is open/latest. The surface reads above take ?period={Index} to view a past one.
accounts.MapGet("/{id:guid}/periods", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(PeriodsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var periods = account.Periods;
    var rows = periods
        .Select((p, i) => new PeriodRowDto(i, p.From, p.To, p.Status == PeriodStatus.Open, i == periods.Count - 1))
        .ToList();
    return Results.Ok(new PeriodsViewDto(account.Currency, periods.Count - 1, rows));
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
        proj.HasUnknownAmounts,
        proj.Months[0].Opening, proj.Months[0].Month, proj.MonthlyCommitted));
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

// Path-B thin Achievements: the full catalogue (earned + locked with progress), same computation as the
// milestones counts above. Read-only. See AchievementsMap / AchievementsView.cs.
accounts.MapGet("/{id:guid}/achievements", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(AchievementsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(AchievementsMap.View(account));
});

// Path-B thin onboarding checklist: the four first-run steps (Done derived from the account) + the dismissed flag.
accounts.MapGet("/{id:guid}/onboarding", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(OnboardingViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(OnboardingMap.View(account));
});

// Dismiss the getting-started card (persist so it stays gone). A former deferred whole-snapshot write, now on the
// mutation spine — mirrors BudgetingState.DismissOnboarding.
accounts.MapPut("/{id:guid}/onboarding/dismissed", async (Guid id, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync(userId, id, account => { account.DismissOnboarding(); return 0; }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, null));
});

// Path-B thin notifications bell: the current-period domain-derived alerts (deficit, over/near budgets, due
// recurring, no income yet). Read-only — each item carries a TargetTab the client can jump to. See NotificationsMap.
accounts.MapGet("/{id:guid}/notifications", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(NotificationsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(NotificationsMap.View(account));
});

// The Insights health read (latest period): the gauge score/band + savings + trend + breakdown numbers, plus the
// narrative as language-independent messages (code + args). The client owns the per-language templates — see InsightsDto.
accounts.MapGet("/{id:guid}/insights", async (Guid id, int? period, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(InsightsDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    if (account.Periods.Count == 0) return Results.Ok(InsightsDto.Empty);
    // Insights reflect the *viewed* period (the thick modal recomputes per period, keyed on PeriodNumber). ?period is
    // the 0-based oldest=0 index shared by the other reads; out-of-range/absent falls back to the latest period.
    var idx = period is int p && p >= 0 && p < account.Periods.Count ? p : account.Periods.Count - 1;
    var report = new InsightsService().Build(account, idx);
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

// --- Command writes (Option-A migration, docs/MOBILE.md) -----------------
// The client still saves whole snapshots (below); these let a thin client send just the command. The server applies
// it through the same domain the reads use — one place for the money maths — via SnapshotService.MutateAsync (the
// server-side read-modify-write). Reads-first discipline holds: these are NOT wired into the web client yet.
// Scope note: settlement (on-behalf) and bank-import provenance are cross-account / bank-flow concerns and are NOT
// mirrored here yet — editing/removing a settlement-linked expense through this API won't keep its counterpart in
// step. The web app's whole-snapshot path still handles those; this first slice is the plain manual capture loop.

// Initialize a freshly-created account's snapshot server-side (the thin-client counterpart of the web app's
// first-load seed). 409 if it's already set up. Body is optional; a client sends its local date so the first period
// lands in the right month.
accounts.MapPost("/{id:guid}/bootstrap", async (Guid id, BootstrapAccountRequest? req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = req?.Today ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var version = await svc.BootstrapAsync(userId, id, today, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, id));
});

accounts.MapPost("/{id:guid}/expenses", async (Guid id, AddExpenseRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);   // for the delta's bank-adjusted overview
    var (version, changed, delta) = await svc.MutateOrSkipAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to add an expense to.");
        // ★ T0 — the retry check, and it runs FIRST, before any validation that could reject the second attempt
        // for a reason the first never hit (a category deleted in between, the period rolled). A retry is a
        // question about a write that already happened; re-litigating its inputs answers the wrong one.
        // ⚠️ Scoped to the open period, not the whole account. A retry arrives seconds after its original, so the
        // period cannot have rolled in between — and searching every period would walk the entire ledger on every
        // expense anyone logs, to catch a case that cannot occur.
        if (req.ClientId is { } key && key != Guid.Empty &&
            period.Expenses.FirstOrDefault(e => e.ClientId == key) is { } already)
        {
            // The original's own delta, computed from the account as it stands now. Same shape, same id, same
            // figures the first response would have carried — which is what makes the retry indistinguishable
            // from success to the client that sent it.
            return (false, (already.Id, SpendingMap.ToDto(account, already), SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency)));
        }
        if (account.FindCategory(req.CategoryId) is null) throw new InvalidOperationException("That category doesn't exist in this account.");
        var fund = account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        var expense = new Expense(req.CategoryId, new Money(req.Amount, account.Currency), req.Date, userId, req.FundId, req.Note,
            onBehalfOfOtherAccount: req.OnBehalfOfOtherAccount);
        expense.SetClientId(req.ClientId is { } k && k != Guid.Empty ? k : null);
        expense.SetFundSynced(fund.IsSynced);   // synced funds aren't debited — the real bank balance handles it
        if (req.TagId is { } addTag && account.FindTag(addTag) is not null) expense.SetTag(addTag);
        // Guarded like the tag: a trip that isn't in this account is dropped rather than stored as a dangling id.
        if (req.TripId is { } addTrip && account.FindTrip(addTrip) is not null) expense.SetTrip(addTrip);
        // F2, learned: the tag takes this expense's category as its binding, if it has none yet. On the ADD route
        // only — the edit route deliberately does not re-file an existing row off a tap meant for labelling, and
        // teaching from one would make the same tap change every FUTURE row instead.
        // ⚠️ Never on a trip expense. On a trip the TRIP owns the filing, so every label lands in the trip's one
        // category; learning there would teach "Stay files into Japan-holiday" and then apply it at home. This is
        // the same guard both clients already apply before pre-selecting a bound category.
        if (expense.TagIds.Count > 0 && expense.TripId is null)
            account.LearnTagCategory(expense.TagIds[0], expense.CategoryId);
        // What was typed before conversion. Display only — Amount is still the single figure every total is built
        // from, and the server does not re-convert: the client already did, once, at entry.
        expense.SetForeign(req.ForeignAmount, req.ForeignCurrency);
        expense.SetTime(req.Time);
        period.AddExpense(expense);
        // F4 round-ups. Same service the web client runs in its optimistic apply, so the two can't produce different
        // savings rows — the config lives on the aggregate, so nothing about it needs to travel in the request.
        new RoundUpService().Sweep(account, period, expense.Amount, expense.Date);
        // The delta a thin client reconciles from (the thick client reads only Version/EntityId — a superset).
        return (true, (expense.Id, SpendingMap.ToDto(account, expense), SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency)));
    }, ct);
    // Announced only when something actually changed. A recognised retry moved nothing, and telling every other
    // client to re-pull for it would undo half the point of recognising it.
    if (changed) await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new ExpenseMutationDto(version, delta.Id, delta.Item2, delta.Item3));
});

accounts.MapPut("/{id:guid}/expenses/{expenseId:guid}", async (Guid id, Guid expenseId, EditExpenseRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, delta) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindCategory(req.CategoryId) is null) throw new InvalidOperationException("That category doesn't exist in this account.");
        var fund = account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        var before = period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        var edited = period.EditExpense(expenseId, req.CategoryId, new Money(req.Amount, account.Currency), req.FundId, req.Note, req.Date);
        edited.SetFundSynced(fund.IsSynced);            // recompute at edit time (moving to/from a synced fund)
        // ★ Provenance survives an edit, badge included (S111, owner report: tagging an auto-filed row lost its 🏦).
        // The badge answers "where did this row come from", which editing does not change — and its tooltip resolves
        // the responsible rule live, so it never goes stale. Clearing it also hid the edit modal's rule shortcut at
        // exactly the moment it is most wanted: you are correcting a row the rule mis-filed.
        edited.SetBankLink(before?.BankExternalId, before?.AutoFiled ?? false);
        // The tag follows the same rule as the time below, and for the same reason: EditExpense carried the stored
        // one across, so an omitted value leaves it alone and clearing is explicit. It did NOT until S111 — an
        // omitted tag cleared the label, which is how the native edit stripped tags for as long as it existed.
        if (req.ClearTag) edited.SetTag(null);
        else if (req.TagId is { } editTag && account.FindTag(editTag) is not null) edited.SetTag(editTag);
        // The time is NOT authoritative-by-omission — EditExpense already carried the stored one across, so only an
        // explicit value or an explicit clear touches it. See EditExpenseRequest.
        if (req.ClearTime) edited.SetTime(null);
        else if (req.Time is { } editTime) edited.SetTime(editTime);

        // Edit is append-only (a new id), so the delta carries the NEW row for the client to swap in.
        return (edited.Id, SpendingMap.ToDto(account, edited), SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency));
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new ExpenseMutationDto(version, delta.Id, delta.Item2, delta.Item3));
});

accounts.MapDelete("/{id:guid}/expenses/{expenseId:guid}", async (Guid id, Guid expenseId, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, overview) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        period.RemoveExpense(expenseId);
        return SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency);   // recomputed, bank-adjusted totals
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new ExpenseMutationDto(version, expenseId, null, overview));
});

// Loan installments (R2). One payment posts several linked expense rows — principal, interest, and any additional
// lines — sharing an installment-group id, so "what did I actually pay in interest?" is a Breakdown slice rather than
// a calculation. The split comes from the loan's own schedule; the typed total and extra lines are ground truth.
accounts.MapPost("/{id:guid}/installments", async (Guid id, LogInstallmentRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, delta) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to log an installment in.");
        var bucket = account.FindSavingCategory(req.BucketId) ?? throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        var fund = account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        foreach (var categoryId in new[] { req.PrincipalCategoryId, req.InterestCategoryId }
                     .Concat((req.Additional ?? []).Select(x => x.CategoryId)))
            if (account.FindCategory(categoryId) is null)
                throw new InvalidOperationException("That category doesn't exist in this account.");

        var extras = (req.Additional ?? []).Select(x =>
            new InstallmentExtra(new Money(x.Amount, account.Currency), x.CategoryId,
                x.TagId is { } t && account.FindTag(t) is not null ? t : null, x.Note)).ToList();

        var rows = period.LogInstallment(bucket, new Money(req.Total, account.Currency), req.Date, userId, req.FundId,
            req.PrincipalCategoryId, req.InterestCategoryId, extras,
            principalTagId: req.PrincipalTagId is { } pt && account.FindTag(pt) is not null ? pt : null,
            interestTagId: req.InterestTagId is { } it && account.FindTag(it) is not null ? it : null,
            note: req.Note,
            fundSynced: fund.IsSynced);

        var groupId = rows[0].InstallmentGroupId!.Value;
        return (groupId, rows.Select(r => SpendingMap.ToDto(account, r)).ToList(),
            SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency));
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new InstallmentMutationDto(version, delta.groupId, delta.Item2, delta.Item3));
});

// Removing an installment removes every row of it — a half-installment (interest kept, principal gone) would be
// worse than either keeping or dropping the whole thing.
accounts.MapDelete("/{id:guid}/installments/{groupId:guid}", async (Guid id, Guid groupId, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);

    // ⚠️ D2 — undoing a cross-account installment has to reverse the principal in the OTHER account, so a pre-read
    // decides the spine. Without it the rows would vanish from the ledger while the balance kept the payment, which
    // is the half-undo RemoveInstallmentGroup exists to prevent.
    Guid? debtElsewhere = null;
    if (await svc.GetAsync(userId, id, ct) is { Payload.Length: > 0 } undoPre
        && AccountSnapshotSerializer.Deserialize(undoPre.Payload).CurrentPeriod?.InstallmentGroup(groupId).FirstOrDefault()
           is { DebtBucketAccountId: { } owner })
        debtElsewhere = ForeignDebtAccount(owner, id);

    AccountOverviewDto Undo(Account account, Account debtAccount)
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var bucketId = period.InstallmentGroup(groupId).FirstOrDefault()?.DebtBucketId;
        period.RemoveInstallmentGroup(groupId, bucketId is { } bid ? debtAccount.FindSavingCategory(bid) : null);
        return SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency);
    }

    long version;
    AccountOverviewDto overview;
    if (debtElsewhere is { } undoDebtAccountId)
    {
        var (v, debtVersion, o) = await svc.MutateTwoAsync(userId, id, undoDebtAccountId, Undo, ct);
        version = v; overview = o;
        await notifier.AccountChangedAsync(undoDebtAccountId, userId, debtVersion);
    }
    else
    {
        (version, overview) = await svc.MutateAsync(userId, id, a => Undo(a, a), ct);
    }
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new InstallmentMutationDto(version, groupId, [], overview));
});

// Income (deposits). A deposit's category is a *contribution* category (or empty = general income); deposits with the
// same (member, category, fund) merge. Edits/removes are the caller's own only (403 otherwise) — deposits are per-member.
accounts.MapPost("/{id:guid}/deposits", async (Guid id, AddDepositRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, changed, delta) = await svc.MutateOrSkipAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to record income in.");
        // ★ T0 — the retry check, before any validation, for the reason spelled out on the add-expense route.
        if (req.ClientId is { } key && key != Guid.Empty &&
            period.Contributions.FirstOrDefault(c => c.ClientId == key) is { } already)
            return (false, (already.Id, SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency)));
        if (req.CategoryId != Guid.Empty && account.FindContributionCategory(req.CategoryId) is null)
            throw new InvalidOperationException("That income category doesn't exist in this account.");
        var fund = req.FundId == Guid.Empty ? null
            : account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        var contribution = period.Deposit(userId, new Money(req.Amount, account.Currency), req.CategoryId, req.FundId, req.Date);
        contribution.SetFundSynced(fund?.IsSynced ?? false);   // synced destination fund isn't credited here
        contribution.SetClientId(req.ClientId is { } k && k != Guid.Empty ? k : null);
        return (true, (contribution.Id, SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency)));
    }, ct);
    if (changed) await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new DepositMutationDto(version, delta.Id, delta.Item2));
});

accounts.MapPut("/{id:guid}/deposits/{depositId:guid}", async (Guid id, Guid depositId, EditDepositRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, overview) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var contribution = period.FindContribution(depositId)
            ?? throw new InvalidOperationException("That deposit doesn't exist in this period.");
        if (contribution.MemberId != userId)
            throw new ForbiddenException("You can only change your own deposits.");
        if (req.CategoryId != Guid.Empty && account.FindContributionCategory(req.CategoryId) is null)
            throw new InvalidOperationException("That income category doesn't exist in this account.");
        var fund = req.FundId == Guid.Empty ? null
            : account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        period.EditContribution(depositId, new Money(req.Amount, account.Currency), req.CategoryId, req.FundId, req.Date);
        period.FindContribution(depositId)?.SetFundSynced(fund?.IsSynced ?? false);   // recompute at edit time
        return SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new DepositMutationDto(version, depositId, overview));
});

accounts.MapDelete("/{id:guid}/deposits/{depositId:guid}", async (Guid id, Guid depositId, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, overview) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var contribution = period.FindContribution(depositId)
            ?? throw new InvalidOperationException("That deposit doesn't exist in this period.");
        if (contribution.MemberId != userId)
            throw new ForbiddenException("You can only change your own deposits.");
        period.RemoveContribution(depositId);
        return SpendingMap.Overview(account, period, bank.Balance, bank.BalanceCurrency);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new DepositMutationDto(version, depositId, overview));
});

// Statement import — commit a batch of reviewed rows in one save: a negative amount posts an expense (its absolute
// value, category read as a spend category), a positive one posts income (category read as a contribution category);
// both attribute to the row's fund and inherit its synced flag. Zero-amount / empty-ref rows are skipped (as the web
// does); a row naming a missing category/fund fails the whole batch (400). The review-step dedupe/in-period gating is
// the caller's (those are reads). Mirrors BudgetingState.ImportTransactions.
accounts.MapPost("/{id:guid}/import", async (Guid id, ImportTransactionsRequest req, ClaimsPrincipal user, SnapshotService svc, EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    // Statement import is Pro (MONETIZATION.md's second built-in upgrade moment). Inert while unlimited/pro.
    await entitlements.RequireAsync(userId, PlanFeatures.Import, ct);
    var (version, result) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to import into.");
        int imported = 0, skipped = 0, duplicates = 0;

        // Snapshot the period's existing (date, amount, fund) keys BEFORE importing, so re-running the same
        // statement skips its rows — but two identical rows within one fresh batch both post (they only match
        // pre-existing data, not each other). Debits match expenses; credits match real (non-carryover) contributions.
        var existingExpenses = req.SkipDuplicates
            ? period.Expenses.Select(e => (e.Date, e.Amount.Amount, e.FundId)).ToHashSet()
            : new HashSet<(DateOnly, decimal, Guid)>();
        var existingIncome = req.SkipDuplicates
            ? period.Contributions.Where(c => c.MemberId != Period.CarryoverSource).Select(c => (c.Date, c.Paid.Amount, c.FundId)).ToHashSet()
            : new HashSet<(DateOnly, decimal, Guid)>();

        foreach (var row in req.Rows)
        {
            if (row.Amount == 0m || row.CategoryId == Guid.Empty || row.FundId == Guid.Empty) { skipped++; continue; }
            var fund = account.FindFund(row.FundId) ?? throw new InvalidOperationException("A row references a fund that doesn't exist in this account.");
            var note = string.IsNullOrWhiteSpace(row.Note) ? null : row.Note;
            if (row.Amount < 0m)
            {
                if (account.FindCategory(row.CategoryId) is null) throw new InvalidOperationException("A row references a spend category that doesn't exist in this account.");
                if (req.SkipDuplicates && existingExpenses.Contains((row.Date, Math.Abs(row.Amount), row.FundId))) { duplicates++; continue; }
                var expense = new Expense(row.CategoryId, new Money(Math.Abs(row.Amount), account.Currency), row.Date, userId, row.FundId, note);
                expense.SetFundSynced(fund.IsSynced);
                period.AddExpense(expense);
            }
            else
            {
                if (account.FindContributionCategory(row.CategoryId) is null) throw new InvalidOperationException("A row references an income category that doesn't exist in this account.");
                if (req.SkipDuplicates && existingIncome.Contains((row.Date, row.Amount, row.FundId))) { duplicates++; continue; }
                var contribution = period.Deposit(userId, new Money(row.Amount, account.Currency), row.CategoryId, row.FundId, row.Date);
                contribution.SetFundSynced(fund.IsSynced);
            }
            imported++;
        }
        return (imported, skipped, duplicates);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new ImportResultDto(version, result.imported, result.skipped, result.duplicates));
});

// Settlement / cross-account writes — the only commands that touch TWO accounts, applied atomically through
// SnapshotService.MutateTwoAsync (both saved in one transaction; the caller must be a contributor on both, same
// currency). Mirrors BudgetingState.TransferToAccount / SettleExpenseToAccount / UnsettleExpense. Both accounts are
// notified. Scope: bank-import provenance (ConfirmBankMoneyOutAsTransfer) stays deferred — it's a prod-only bank flow.
accounts.MapPost("/{id:guid}/transfers-out", async (Guid id, TransferToAccountRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var date = req.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, destVersion, changed, transferId) = await svc.MutateTwoOrSkipAsync(userId, id, req.DestinationAccountId, (source, dest) =>
    {
        // ★ T0 — the retry check, before any validation, and on the SOURCE side only: the pair is written in one
        // two-account mutation, so an outflow carrying the key proves its deposit landed with it. Skipping is
        // all-or-nothing for the same reason (see MutateTwoOrSkipAsync).
        if (req.ClientId is { } key && key != Guid.Empty && source.CurrentPeriod is { } open &&
            open.ExternalTransfers.FirstOrDefault(t => t.ClientId == key) is { } already)
            return (false, already.Id);
        if (req.Amount <= 0m) throw new InvalidOperationException("The amount must be positive.");
        if (!string.Equals(source.Currency, dest.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Both accounts must use the same currency.");
        var sourceFund = source.FindFund(req.FromFundId) ?? throw new InvalidOperationException("The source fund doesn't exist in this account.");
        var sourcePeriod = source.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var destPeriod = dest.CurrentPeriod ?? throw new InvalidOperationException("The other account has no open period to receive the transfer.");

        // Outflow here (caps at the source fund's balance); a synced source keeps its real bank balance so it's informational.
        var outflow = sourcePeriod.TransferOut(req.FromFundId, new Money(req.Amount, source.Currency), date, req.DestinationAccountId, req.Note);
        outflow.SetFundSynced(sourceFund.IsSynced);
        // One id on both halves, so either side can find (and edit, and delete) the other later. Minted here
        // because this is the only place a pair is created — see ExternalTransfer.AccountTransferId.
        var pairId = Guid.NewGuid();
        outflow.SetAccountTransferLink(pairId);
        // Optional, and only about budgets — see TransferToAccountRequest. Guarded like the expense tag: a category
        // that isn't in this account is dropped rather than stored as a dangling id.
        if (req.CategoryId is { } outCat && source.FindCategory(outCat) is not null) outflow.SetCategory(outCat);

        // Deposit there, into the resolved destination fund (empty → first unsynced, else first fund).
        var destFundId = req.DestinationFundId != Guid.Empty && dest.RootFunds.Any(f => f.Id == req.DestinationFundId)
            ? req.DestinationFundId
            : (dest.RootFunds.FirstOrDefault(f => !f.IsSynced) ?? dest.RootFunds.FirstOrDefault())?.Id
              ?? throw new InvalidOperationException("The other account has no fund to receive the transfer.");
        var destDeposit = destPeriod.Deposit(userId, new Money(req.Amount, dest.Currency), Guid.Empty, destFundId, date);
        destDeposit.SetFundSynced(dest.FindFund(destFundId)?.IsSynced ?? false);
        destDeposit.SetAccountTransferLink(pairId, id);
        // ⚠️ The key goes on the outflow only, NOT on the deposit. The deposit's own key would collide with an
        // ordinary income write in the destination account carrying the same one, and the pair already shares
        // pairId — a second identity for the same two rows is one more thing that can disagree.
        outflow.SetClientId(req.ClientId is { } k && k != Guid.Empty ? k : null);
        return (true, outflow.Id);
    }, ct);
    if (changed)
    {
        await notifier.AccountChangedAsync(id, userId, version);
        await notifier.AccountChangedAsync(req.DestinationAccountId, userId, destVersion);
    }
    return Results.Ok(new MutationResultDto(version, transferId));
});

// Editing and deleting an account-to-account transfer. Both halves move together, in ONE two-account mutation, so
// there is no window where the money exists on one side and not the other. Only transfers carrying a link id can be
// handled here (see ExternalTransfer.AccountTransferId): legacy one-sided rows keep the old delete-this-side-only
// route, which is why the plain transfer DELETE below stays.
// The route id is the PAIR id (ExternalTransfer.AccountTransferId), not the row id, and the destination account
// rides in the body — that way the two accounts are known before the mutation opens, with no extra snapshot load.
// It is not a trust decision: MutateTwoAsync re-checks membership of BOTH accounts, and the pair id has to match a
// row in each, so naming the wrong account simply fails to find a counterpart.
accounts.MapPut("/{id:guid}/account-transfers/{pairId:guid}", async (Guid id, Guid pairId, EditAccountTransferRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, destVersion, _) = await svc.MutateTwoAsync<object?>(userId, id, req.DestinationAccountId, (from, to) =>
    {
        if (req.Amount <= 0m) throw new InvalidOperationException("The amount must be positive.");
        var outgoing = from.FindAccountTransferOut(pairId)
            ?? throw new InvalidOperationException("That transfer no longer exists.");
        var incoming = to.FindAccountTransferIn(pairId)
            ?? throw new InvalidOperationException("The matching deposit in the other account no longer exists.");
        // Both periods must be open: editing into a closed period would silently rewrite a settled month.
        if (outgoing.Period.Status != PeriodStatus.Open || incoming.Period.Status != PeriodStatus.Open)
            throw new InvalidOperationException("One side of this transfer is in a closed period and can't be changed.");

        var fundId = req.FromFundId != Guid.Empty ? req.FromFundId : outgoing.Transfer.FundId;
        if (from.FindFund(fundId) is not { } fund) throw new InvalidOperationException("The source fund doesn't exist in this account.");
        var date = req.Date ?? outgoing.Transfer.Date;
        // Headroom is measured with this transfer's own amount added back, or raising it by any amount would be
        // refused against a balance that already has the old figure deducted.
        var headroom = outgoing.Period.FundBalance(fundId) + (fundId == outgoing.Transfer.FundId ? outgoing.Transfer.Amount : Money.Zero(from.Currency));
        if (req.Amount > headroom.Amount)
            throw new InvalidOperationException($"That fund only holds {headroom}; move money into it from another fund first.");

        outgoing.Transfer.Update(new Money(req.Amount, from.Currency), date, fundId, req.Note);
        outgoing.Transfer.SetFundSynced(fund.IsSynced);
        // Only when the field is present: absent means "leave it alone", so an older client editing the amount can't
        // silently wipe a category. Guid.Empty and an id this account doesn't own both clear it — the same guard the
        // create path uses, rather than storing a dangling reference. Idempotent, as MutateTwoAsync requires.
        if (req.CategoryId is { } cat)
            outgoing.Transfer.SetCategory(from.FindCategory(cat) is not null ? cat : null);
        var destFundId = req.DestinationFundId != Guid.Empty && to.RootFunds.Any(f => f.Id == req.DestinationFundId)
            ? req.DestinationFundId
            : incoming.Deposit.FundId;
        incoming.Period.EditContribution(incoming.Deposit.Id, new Money(req.Amount, to.Currency), incoming.Deposit.CategoryId, destFundId, date);
        incoming.Deposit.SetFundSynced(to.FindFund(destFundId)?.IsSynced ?? false);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    await notifier.AccountChangedAsync(req.DestinationAccountId, userId, destVersion);
    return Results.Ok(new MutationResultDto(version, pairId));
});

accounts.MapDelete("/{id:guid}/account-transfers/{pairId:guid}", async (Guid id, Guid pairId, Guid destinationAccountId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, destVersion, _) = await svc.MutateTwoAsync<object?>(userId, id, destinationAccountId, (from, to) =>
    {
        var outgoing = from.FindAccountTransferOut(pairId)
            ?? throw new InvalidOperationException("That transfer no longer exists.");
        var incoming = to.FindAccountTransferIn(pairId)
            ?? throw new InvalidOperationException("The matching deposit in the other account no longer exists.");
        if (outgoing.Period.Status != PeriodStatus.Open || incoming.Period.Status != PeriodStatus.Open)
            throw new InvalidOperationException("One side of this transfer is in a closed period and can't be removed.");
        outgoing.Period.RemoveExternalTransfer(outgoing.Transfer.Id);
        incoming.Period.RemoveContribution(incoming.Deposit.Id);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    await notifier.AccountChangedAsync(destinationAccountId, userId, destVersion);
    return Results.Ok(new MutationResultDto(version, pairId));
});

accounts.MapPost("/{id:guid}/expenses/{expenseId:guid}/settle", async (Guid id, Guid expenseId, SettleExpenseRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, destVersion, settlementId) = await svc.MutateTwoAsync(userId, id, req.DestinationAccountId, (source, dest) =>
    {
        if (req.Amount <= 0m) throw new InvalidOperationException("The amount must be positive.");
        if (!string.Equals(source.Currency, dest.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Both accounts must use the same currency.");
        var sourcePeriod = source.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var expense = sourcePeriod.Expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this period.");
        if (new Money(req.Amount, source.Currency) > expense.OriginalAmount)
            throw new InvalidOperationException($"You can settle at most {expense.OriginalAmount}.");
        var destPeriod = dest.CurrentPeriod ?? throw new InvalidOperationException("The other account has no open period to receive the expense.");

        var sid = expense.SettlementId ?? Guid.NewGuid();
        var note = string.IsNullOrWhiteSpace(req.Note) ? $"On behalf — from {source.Name}" : req.Note;
        var destCategoryId = req.DestinationCategoryId != Guid.Empty && dest.Categories.Any(c => c.Id == req.DestinationCategoryId)
            ? req.DestinationCategoryId
            : dest.RootCategories.FirstOrDefault()?.Id ?? throw new InvalidOperationException("The other account has no category to record the expense against.");
        var destFundId = req.DestinationFundId != Guid.Empty && dest.RootFunds.Any(f => f.Id == req.DestinationFundId)
            ? req.DestinationFundId
            : (dest.RootFunds.FirstOrDefault(f => !f.IsSynced) ?? dest.RootFunds.FirstOrDefault())?.Id
              ?? throw new InvalidOperationException("The other account has no fund to record the expense against.");

        // Re-settle replaces the prior linked destination expense so the amount can't double up.
        if (destPeriod.Expenses.FirstOrDefault(e => e.SettlementId == sid) is { } existing)
            destPeriod.RemoveExpense(existing.Id);
        destPeriod.AddExpense(new Expense(destCategoryId, new Money(req.Amount, dest.Currency), today, userId, destFundId,
            note, settlementId: sid, settledFromAccountId: id));
        sourcePeriod.SetSettlement(expenseId, sid, req.DestinationAccountId, new Money(req.Amount, source.Currency));
        return sid;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    await notifier.AccountChangedAsync(req.DestinationAccountId, userId, destVersion);
    return Results.Ok(new MutationResultDto(version, settlementId));
});

// Undo a settlement from the source side: remove the linked destination expense and restore this expense's full amount.
// The destination account is passed explicitly (the caller holds it as the expense's SettledToAccountId).
accounts.MapDelete("/{id:guid}/expenses/{expenseId:guid}/settle", async (Guid id, Guid expenseId, Guid destinationAccountId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, destVersion, _) = await svc.MutateTwoAsync<object?>(userId, id, destinationAccountId, (source, dest) =>
    {
        var sourcePeriod = source.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var expense = sourcePeriod.Expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this period.");
        if (!expense.IsSettlementSource || expense.SettledToAccountId != destinationAccountId || expense.SettlementId is not { } sid)
            throw new InvalidOperationException("That expense isn't settled onto this account.");
        foreach (var p in dest.Periods)
            if (p.Expenses.FirstOrDefault(e => e.SettlementId == sid) is { } linked) { p.RemoveExpense(linked.Id); break; }
        sourcePeriod.SetSettlement(expenseId, sid, destinationAccountId, new Money(0m, source.Currency));   // restores the full amount
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    await notifier.AccountChangedAsync(destinationAccountId, userId, destVersion);
    return Results.Ok(new MutationResultDto(version, expenseId));
});

// Money back on an expense — a refund, or a friend's share of a split bill landing as a bank credit. The expense
// shrinks; nothing is booked as income (see Expense.RefundedAmount for why that distinction is the whole feature).
//
// ★ The body carries the amount that came back NOW, not the running total, and the server adds it inside the
// mutation. The domain takes a total (so it stays order-independent and re-runnable), but making the *client* send
// one would force a read-modify-write: two phones acking two credits against the same dinner would each compute a
// total from the figure they last read and the second would erase the first. Deltas make the lock do that work.
//
// ⚠️ The expense id CHANGES. The ledger is append-only, so reducing an amount mints a new row — which is why the
// new id comes back in the response rather than leaving the caller holding one that no longer resolves. The undo
// below is addressed by the new id; the S108 lesson was an undo unreachable by construction from a thin client, and
// an id the client cannot learn is the same bug wearing a different hat.
accounts.MapPost("/{id:guid}/expenses/{expenseId:guid}/refund", async (Guid id, Guid expenseId, RefundExpenseRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, newId) = await svc.MutateAsync(userId, id, account =>
    {
        if (req.Amount <= 0m) throw new InvalidOperationException("The amount must be positive.");
        // ★ ANY period, not just the open one — the money often comes back months after the purchase (owner
        // report: paid for a group in June, a member handed their share back in August). Where the credit then
        // has to land is the interesting half, and it is decided in Account.RefundExpense.
        var expense = account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this account.");
        // Domain guards the ceiling (0 ≤ total ≤ the original charge) and says the figure in its message. ToFundId is
        // where the money actually arrived — only meaningful when that is a different wallet; see Account.RefundExpense.
        return account.RefundExpense(expenseId, new Money(expense.RefundedAmount + req.Amount, account.Currency), req.ToFundId).Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, newId));
});

// Undo: put the whole charge back. Addressed by the expense's CURRENT id (the one the last refund returned, or the
// one /spending reports now) — and it mints another new id, returned the same way.
accounts.MapDelete("/{id:guid}/expenses/{expenseId:guid}/refund", async (Guid id, Guid expenseId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, newId) = await svc.MutateAsync(userId, id, account =>
    {
        var expense = account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this account.");
        if (!expense.IsRefunded) throw new InvalidOperationException("Nothing has come back on that expense.");
        // ⚠️ Through RefundExpense, not straight to the period's SetRefund. Undoing a cross-period refund must
        // also take the money back out of this period's opening balance — the half a bare SetRefund would skip,
        // leaving the account permanently richer by the amount that was undone.
        return account.RefundExpense(expenseId, new Money(0m, account.Currency), expense.FundId).Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, newId));
});

// Budgets — a category's planned allocation for the open period. One idempotent upsert (create-or-update, mirroring
// BudgetingState.SaveBudget → Period.SetBudget) keyed by category, plus a remove. Budgets are advisory: they don't
// reserve cash and are never capped, so this never rejects for being "too big" (only a negative amount → 400). The
// threshold arrives as a percent (0–100) and is stored as a fraction, matching the web's SaveBudget contract.
accounts.MapPut("/{id:guid}/budgets/{categoryId:guid}", async (Guid id, Guid categoryId, SetBudgetRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, view) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to budget in.");
        if (account.FindCategory(categoryId) is null) throw new InvalidOperationException("That category doesn't exist in this account.");
        period.SetBudget(categoryId, new Money(req.Amount, account.Currency), req.ThresholdPercent / 100m, req.NotifyEvery);
        return BudgetsMap.View(account, 0);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new BudgetMutationDto(version, categoryId, view with { Version = version }));
});

accounts.MapDelete("/{id:guid}/budgets/{categoryId:guid}", async (Guid id, Guid categoryId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, view) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        period.RemoveBudget(categoryId);   // 400 if no budget exists for the category in this period
        return BudgetsMap.View(account, 0);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new BudgetMutationDto(version, categoryId, view with { Version = version }));
});

// Reallocation. to-savings mirrors the web's live "Move it to the loan" nudge (BudgetingState.ReallocateBudgetToSaving):
// it sets the budget to an absolute NewBudget and earmarks Amount to a bucket in one save (advisory — uncapped, matching
// the client). to-budget exposes the domain BudgetReallocationService.ToBudget: move a budget's leftover into another,
// capped so a budget can't drop below what's already spent (no web UI yet, but the tested domain capability).
accounts.MapPost("/{id:guid}/reallocations/to-savings", async (Guid id, ReallocateToSavingsRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var date = req.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindCategory(req.CategoryId) is null) throw new InvalidOperationException("That category doesn't exist in this account.");
        if (account.FindSavingCategory(req.SavingCategoryId) is null) throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        period.SetBudget(req.CategoryId, new Money(req.NewBudget, account.Currency), req.ThresholdPercent / 100m, req.NotifyEvery);
        period.AllocateToSavings(req.SavingCategoryId, new Money(req.Amount, account.Currency), date);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, req.SavingCategoryId));
});

accounts.MapPost("/{id:guid}/reallocations/to-budget", async (Guid id, ReallocateToBudgetRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        // ToBudget validates both budgets exist, the categories differ, and the amount is positive and ≤ leftover → 400.
        new FinApp.Domain.Services.BudgetReallocationService()
            .ToBudget(account, period, req.FromCategoryId, req.ToCategoryId, new Money(req.Amount, account.Currency));
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, req.ToCategoryId));
});

// Savings money-movements (mirroring BudgetingState AllocateSaving/EditSavingDeposit/RemoveSavingDeposit/SpendFromSavings).
// A savings deposit earmarks money within the balance (raises "saved", lowers "free"); it never leaves the account.
// Spend-from-savings records a real expense AND a matching negative drawdown, so the earmark and the balance both fall.
// (Bucket/goal CRUD is a separate later slice; the "priorSaved" the client passes is unused by the domain, so it's omitted.)
accounts.MapPost("/{id:guid}/savings/deposits", async (Guid id, AddSavingDepositRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, changed, delta) = await svc.MutateOrSkipAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to save into.");
        // ★ T0 — the retry check, before any validation (see the add-expense route).
        if (req.ClientId is { } key && key != Guid.Empty &&
            period.SavingAllocations.FirstOrDefault(a => a.ClientId == key) is { } already)
            return (false, (already.Id, SavingsMap.View(account, 0, bank.Balance, bank.BalanceCurrency)));
        if (account.FindSavingCategory(req.SavingCategoryId) is null)
            throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        var allocation = period.AllocateToSavings(req.SavingCategoryId, new Money(req.Amount, account.Currency), req.Date, req.Note);
        allocation.SetClientId(req.ClientId is { } k && k != Guid.Empty ? k : null);
        return (true, (allocation.Id, SavingsMap.View(account, 0, bank.Balance, bank.BalanceCurrency)));
    }, ct);
    if (changed) await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new SavingsMutationDto(version, delta.Id, delta.Item2 with { Version = version }));
});

accounts.MapPut("/{id:guid}/savings/deposits/{allocationId:guid}", async (Guid id, Guid allocationId, EditSavingDepositRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        period.EditSavingDeposit(allocationId, new Money(req.Amount, account.Currency));
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, allocationId));
});

accounts.MapDelete("/{id:guid}/savings/deposits/{allocationId:guid}", async (Guid id, Guid allocationId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        period.RemoveSavingAllocation(allocationId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, allocationId));
});

accounts.MapPost("/{id:guid}/savings/spend", async (Guid id, SpendFromSavingsRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, expenseId) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period to spend from.");
        if (account.FindSavingCategory(req.SavingCategoryId) is null)
            throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        if (account.FindCategory(req.CategoryId) is null)
            throw new InvalidOperationException("That category doesn't exist in this account.");
        // Explicit fund, or the web default: the first spendable (non-synced, non-archived) top-level fund.
        var fund = req.FundId != Guid.Empty
            ? account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.")
            : account.RootFunds.FirstOrDefault(f => !f.IsSynced && !f.IsArchived) ?? account.RootFunds.FirstOrDefault()
              ?? throw new InvalidOperationException("This account has no fund to spend from.");
        var expense = period.ConvertSavingToExpense(req.SavingCategoryId, req.CategoryId, new Money(req.Amount, account.Currency), req.Date, userId, fund.Id, req.Note);
        expense.SetFundSynced(fund.IsSynced);   // correct for an explicitly-chosen synced fund (the web default never is)
        return expense.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, expenseId));
});

// Savings-bucket CRUD/config (mirroring BudgetingState AddSavingBucket/SaveSavingBucket + archive/remove). The 18-field
// upsert is applied by the shared SavingBucketConfig.Apply so create and update can't drift. A debt bucket's stated
// balance is anchored to the server's UTC date. Money-movements between/out of buckets are a separate slice.
accounts.MapPost("/{id:guid}/savings/buckets", async (Guid id, SaveSavingBucketRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, bucketId) = await svc.MutateAsync(userId, id, account =>
    {
        var bucket = account.AddSavingCategory(req.Name);   // enforces a unique name
        SavingBucketConfig.Apply(account, bucket.Id, req, today);
        return bucket.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, bucketId));
});

accounts.MapPut("/{id:guid}/savings/buckets/{bucketId:guid}", async (Guid id, Guid bucketId, SaveSavingBucketRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        if (account.FindSavingCategory(bucketId) is null)
            throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        SavingBucketConfig.Apply(account, bucketId, req, today);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, bucketId));
});

accounts.MapPut("/{id:guid}/savings/buckets/{bucketId:guid}/archived", async (Guid id, Guid bucketId, SetArchivedRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.SetSavingArchived(bucketId, req.Archived);   // throws if the bucket is missing
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, bucketId));
});

accounts.MapDelete("/{id:guid}/savings/buckets/{bucketId:guid}", async (Guid id, Guid bucketId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveSavingCategory(bucketId);   // throws on a removal blocker (sub-buckets / savings activity) or if missing
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, bucketId));
});

// Savings bucket money-movements (mirroring DisburseSaving/ConvertSavingToBudget/MoveSavingToBucket + the undo). These
// complete the savings story: deploy a bucket to its goal (money out), mature a save into a budget (no money moves),
// move between buckets (net-neutral), and undo any of them.
// Scope note: like the web, the domain does NOT enforce "can't deploy more than the bucket holds" — the caller owns that.
accounts.MapPost("/{id:guid}/savings/disburse", async (Guid id, DisburseSavingRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, transferId) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindSavingCategory(req.SavingCategoryId) is null)
            throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        var fund = account.FindFund(req.FundId) ?? throw new InvalidOperationException("That fund doesn't exist in this account.");
        var transfer = period.DisburseSaving(req.SavingCategoryId, req.FundId, new Money(req.Amount, account.Currency), req.Date, req.Note);
        transfer.SetFundSynced(fund.IsSynced);   // a synced fund's real balance already reflects the outflow
        // On a debt bucket, deploying to the bank is an extra payment on top of the schedule (no-op for other kinds).
        account.RecordSavingDebtPayment(req.SavingCategoryId, req.Amount, req.Date);
        return transfer.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, transferId));
});

accounts.MapPost("/{id:guid}/savings/to-budget", async (Guid id, ConvertSavingToBudgetRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindSavingCategory(req.SavingCategoryId) is null)
            throw new InvalidOperationException("That savings bucket doesn't exist in this account.");
        if (account.FindCategory(req.CategoryId) is null)
            throw new InvalidOperationException("That category doesn't exist in this account.");
        period.ConvertSavingToBudget(req.SavingCategoryId, req.CategoryId, new Money(req.Amount, account.Currency), req.Date, req.Note);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, req.SavingCategoryId));
});

accounts.MapPost("/{id:guid}/savings/transfer", async (Guid id, MoveSavingsRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindSavingCategory(req.FromBucketId) is null || account.FindSavingCategory(req.ToBucketId) is null)
            throw new InvalidOperationException("A savings bucket in the move doesn't exist in this account.");
        period.TransferSavings(req.FromBucketId, req.ToBucketId, new Money(req.Amount, account.Currency), req.Date, req.Note);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, req.ToBucketId));
});

accounts.MapDelete("/{id:guid}/savings/movements/{allocationId:guid}", async (Guid id, Guid allocationId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        // The bucket travels so a deployed-to-a-loan disbursement can put the principal back as well as the money.
        // Looked up before the removal, because the movement is gone by the time the call returns.
        var bucket = period.SavingAllocations.FirstOrDefault(a => a.Id == allocationId) is { } mv
            ? account.FindSavingCategory(mv.SavingCategoryId)
            : null;
        period.RemoveSavingMovement(allocationId, bucket);   // undoes a to-budget / transfer / disburse (throws if missing)
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, allocationId));
});

// --- Account structure: spend categories, funds, contribution categories -------------------------------------
// Straight CRUD on the aggregate (mirroring BudgetingState Add/Edit/Archive/Remove). All domain guards — unique
// names, valid parents, removal blockers (references / sub-items / last fund) — surface as 400. Fund transfers and
// opening balances are period money-movements, a separate later slice; archived here is a plain hide (no balance move).

// Spend categories
accounts.MapPost("/{id:guid}/categories", async (Guid id, CreateCategoryRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, categoryId) = await svc.MutateAsync(userId, id, account =>
    {
        // ⚠️ req.ParentId is deliberately NOT passed: nesting cannot be created (see Account.AddCategory), and the
        // old comment here claimed this call "validates parent", which it never did. The field stays on the wire
        // for older clients that still send one — it is ignored, and the category comes back top-level.
        var category = account.AddCategory(req.Name, req.Icon);   // validates the unique name
        if (req.Essential) account.SetCategoryEssential(category.Id, true);
        return category.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, categoryId));
});

accounts.MapPut("/{id:guid}/categories/{categoryId:guid}", async (Guid id, Guid categoryId, EditCategoryRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RenameCategory(categoryId, req.Name);   // throws if missing / duplicate name
        account.SetCategoryIcon(categoryId, req.Icon);
        if (req.Essential is { } essential) account.SetCategoryEssential(categoryId, essential);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, categoryId));
});

accounts.MapPut("/{id:guid}/categories/{categoryId:guid}/archived", async (Guid id, Guid categoryId, SetArchivedRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.SetCategoryArchived(categoryId, req.Archived);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, categoryId));
});

// `moveTo` is how a category with history gets deleted: its expenses (and its sub-categories') are re-filed under
// that category, keeping every row's id and figures, and the caps are dropped. Without it the plain removal rules
// apply and a referenced category is refused — the caller has to say where the history goes.
accounts.MapDelete("/{id:guid}/categories/{categoryId:guid}", async (Guid id, Guid categoryId, Guid? moveTo, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        if (moveTo is { } target && target != Guid.Empty) account.RemoveCategoryReassigning(categoryId, target);
        else account.RemoveCategory(categoryId);   // 400 on a removal blocker (sub-categories / budget / expense refs)
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, categoryId));
});

// Tags — flat, cross-cutting labels attached to expenses (sit alongside sub-categories). Definitions live on the
// aggregate and travel in the snapshot; attaching a tag to an expense rides the add/edit-expense endpoints.

// The manage read. The picker's list (SpendingViewDto.Tags) is built from ActiveTags, so a thin client working
// only from that could archive a tag and never see it again — an archive that is really a delete. This one shows
// archived tags too, which is the only reason it is a separate route rather than a flag on the other one.
accounts.MapGet("/{id:guid}/tags", async (Guid id, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(TagsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(TagsMap.View(account, snap.Version));
});

accounts.MapPost("/{id:guid}/tags", async (Guid id, CreateTagRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, tagId) = await svc.MutateAsync(userId, id, account =>
    {
        var tag = account.AddTag(req.Name, req.Icon, req.IsTripTag);   // 400 on duplicate name
        return tag.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tagId));
});

accounts.MapPut("/{id:guid}/tags/{tagId:guid}", async (Guid id, Guid tagId, EditTagRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RenameTag(tagId, req.Name);   // throws if missing / duplicate name
        account.SetTagIcon(tagId, req.Icon);
        account.SetTagCategory(tagId, req.CategoryId);   // F2; throws if the category isn't in this account
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tagId));
});

accounts.MapPut("/{id:guid}/tags/{tagId:guid}/archived", async (Guid id, Guid tagId, SetArchivedRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.SetTagArchived(tagId, req.Archived);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tagId));
});

accounts.MapDelete("/{id:guid}/tags/{tagId:guid}", async (Guid id, Guid tagId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveTag(tagId);   // hard delete; tagged expenses keep the (now-dangling) id, which stops resolving
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tagId));
});

// Trips — a named journey expenses point at. Membership is by link, never by date, so none of these endpoints
// touches an expense's own period, amount or budget impact.

// The read side. Everything below this was write-only until it existed: the thick client reads trips out of the
// snapshot it already carries, so five commands shipped with no way for a thin client to see what they had done —
// which is why Android had no trips at all rather than a smaller version of them.
// `today` is the caller's own local date (as on /bootstrap): whether a trip is running is a question about the
// traveller's day, and a server in UTC would flip a trip's state hours early or late for half the world.
// Which of the caller's accounts is on a journey right now — one row each, nothing for accounts that aren't.
// ⚠️ NOT a field on the account list, and that is a cost decision rather than a tidiness one: trips are body data,
// so answering this means a snapshot read per account, and a snapshot read is a KMS unwrap (network round-trip)
// plus a gunzip of a payload measured in hundreds of KB. The account list is fetched at startup and on every
// switch; this is fetched only when something asks, and the client caches it for the session.
// ⚠️ Route order/shape: "active-trips" cannot collide with "/{id:guid}" because of the guid constraint.
// `today` is the caller's own local date, for the same reason as /trips: whether a trip is running is a question
// about the traveller's day, and a server in UTC flips it hours early or late for half the world.
accounts.MapGet("/active-trips", async (DateOnly? today, ClaimsPrincipal user, AccountService accountSvc, SnapshotService svc, CancellationToken ct) =>
{
    var on = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var summaries = await accountSvc.ListForUserAsync(user.UserId(), ct);
    var running = new List<ActiveTripDto>();
    foreach (var summary in summaries)
    {
        // ⚠️ Per-account try/catch on purpose. One unreadable or half-migrated snapshot must cost its own badge and
        // nothing else — a 500 here would break the account switcher for every account the user has.
        try
        {
            var snap = await svc.GetAsync(user.UserId(), summary.Id, ct);
            if (string.IsNullOrEmpty(snap.Payload)) continue;
            var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
            if (account.TripsByDeparture.FirstOrDefault(t => t.IsActiveOn(on)) is { } active)
                running.Add(new ActiveTripDto(summary.Id, active.Name, active.Icon));
        }
        catch { /* this account simply reports no journey */ }
    }
    return Results.Ok(running);
});

accounts.MapGet("/{id:guid}/trips", async (Guid id, DateOnly? today, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.Ok(TripsViewDto.Empty);
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    return Results.Ok(TripsMap.View(account, snap.Version, today ?? DateOnly.FromDateTime(DateTime.UtcNow),
        await TripFanOutAsync(svc, account, id, null, ct), id));
});

// One trip opened up — the split behind its total, and every expense linked to it. Its own read rather than
// fields on the list: the list would otherwise carry every expense of every journey the account has ever taken
// to draw one card the reader may never open.
accounts.MapGet("/{id:guid}/trips/{tripId:guid}", async (Guid id, Guid tripId, DateOnly? today, ClaimsPrincipal user, SnapshotService svc, CancellationToken ct) =>
{
    var snap = await svc.GetAsync(user.UserId(), id, ct);
    if (string.IsNullOrEmpty(snap.Payload)) return Results.NotFound();
    var account = AccountSnapshotSerializer.Deserialize(snap.Payload);
    var detailFanOut = await TripFanOutAsync(svc, account, id, tripId, ct) is { } fan ? fan.GetValueOrDefault(tripId) : null;
    return TripsMap.Detail(account, snap.Version, tripId, today ?? DateOnly.FromDateTime(DateTime.UtcNow), detailFanOut, id) is { } detail
        ? Results.Ok(detail)
        : Results.NotFound();
});

// D1: gather the expenses other accounts hold against this account's trips.
// ★ Bounded by each trip's own SourceAccountIds directory, which the attach writes — without it, building one
// trip's total would mean deserializing every account the viewer contributes to, on every view, growing with
// their account count. Almost every account has an empty directory and does no extra work at all.
// ⚠️ The read deliberately does NOT check the viewer's membership of the source account — see
// SnapshotService.GetTripFanOutAsync for the argument. Pass `onlyTrip` to fan out for a single trip.
// ⚠️ `accountId` is the ROUTE's id, never the deserialized aggregate's. The two agree in practice — the client
// serializes the account it loaded — but the aggregate's Id comes out of the payload, and every cross-account
// link in this file (transfer pairs, settlements, and the trip link itself) is written with the route id. Reading
// with a different one finds nothing, silently, and the trip just looks like it has no shared spend.
static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ForeignTripExpense>>?> TripFanOutAsync(
    SnapshotService svc, Account account, Guid accountId, Guid? onlyTrip, CancellationToken ct)
{
    var trips = account.Trips.Where(t => t.SourceAccountIds.Count > 0 && (onlyTrip is not { } o || t.Id == o)).ToList();
    if (trips.Count == 0) return null;
    var map = new Dictionary<Guid, IReadOnlyList<ForeignTripExpense>>();
    foreach (var trip in trips)
    {
        var rows = new List<ForeignTripExpense>();
        foreach (var sourceId in trip.SourceAccountIds)
            rows.AddRange(await svc.GetTripFanOutAsync(sourceId, trip.Id, accountId, ct));
        if (rows.Count > 0) map[trip.Id] = rows;
    }
    return map.Count == 0 ? null : map;
}

accounts.MapPost("/{id:guid}/trips", async (Guid id, CreateTripRequest req, ClaimsPrincipal user, SnapshotService svc,
        EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    // ★ Trips are Pro, whole (owner's call — MONETIZATION.md). This used to allow Free one LIVE trip and charge only
    // for planning a second while the first ran; the feature is now behind the gate from the first one. READING
    // stays free at the GETs above, always: a downgrade must never hide a journey somebody already recorded.
    await entitlements.RequireAsync(userId, PlanFeatures.Trips, ct);

    var (version, tripId) = await svc.MutateAsync(userId, id, account =>
    {
        var trip = account.AddTrip(req.Name, req.From, req.To, req.Destination, req.Icon);   // 400 on duplicate name / bad dates
        return trip.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

// Confirm a departure (or take it back). Trip mode never switches itself on — see Trip.StartedOn.
// ⚠️ NOT gated. Confirming a departure creates nothing and moves no date — it is how a trip that already exists
// gets under way, and a Free account has to be able to run one it made while it was Pro to the end. See
// PlanFeatures.Trips for the whole line.
accounts.MapPut("/{id:guid}/trips/{tripId:guid}/started", async (Guid id, Guid tripId, StartTripRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        // Server date, like finishing: "we've left" is a fact about now.
        if (req.Started) account.StartTrip(tripId, DateOnly.FromDateTime(DateTime.UtcNow));
        else account.UnstartTrip(tripId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

// Finish / reopen. See FinishTripRequest for why this isn't a field on the edit form's full-replace payload.
// ⚠️ NOT gated, in EITHER direction (owner's call). Ending a journey is the exit, and an exit is never sold: a
// trip left running because the subscription lapsed would go on wearing the app's trip mode and dividing its
// spend by a length nobody travelled.
// ★ Finishing early DOES pull `To` in to today — but that is the mechanic of ending early, not an edit: Finish
// can only ever SHORTEN a trip, never push its end out, so it is no route around the gate on the edit form.
// Reopen is ungated for the same reason detach is: finishing pulls a date in irreversibly, so a Free account that
// taps it by accident must be able to undo it. Reopen grants nothing durable either — the trip's own `To` still
// re-finishes it the moment the day passes.
accounts.MapPut("/{id:guid}/trips/{tripId:guid}/finished", async (Guid id, Guid tripId, FinishTripRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        // The server's own date, not the client's: "over" is a fact about now, and a device with a wrong clock
        // would otherwise write an end date the rest of the account disagrees with.
        if (req.Finished) account.FinishTrip(tripId, DateOnly.FromDateTime(DateTime.UtcNow));
        else account.ReopenTrip(tripId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

// Release saved money into the trip's budget: one mutation covering both halves (the period's money movement and
// the trip's record of it), so a failure can't leave a budget raised with no trip line to explain it.
accounts.MapPost("/{id:guid}/trips/{tripId:guid}/use-savings", async (Guid id, Guid tripId, UseTripSavingsRequest req, ClaimsPrincipal user, SnapshotService svc,
        EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    // Funding a trip from a savings pot is the planning half of trips, which is where Pro sits.
    await entitlements.RequireAsync(userId, PlanFeatures.Trips, ct);
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var trip = account.FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("No open period to budget into.");
        // Validates the link, the category and the amount before any money moves.
        account.ApplyTripSavings(tripId, req.Amount);
        period.ConvertSavingToBudget(trip.SavingCategoryId!.Value, trip.CategoryId!.Value,
            new Money(req.Amount, account.Currency), req.Date, req.Note);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

accounts.MapPut("/{id:guid}/trips/{tripId:guid}", async (Guid id, Guid tripId, EditTripRequest req, ClaimsPrincipal user, SnapshotService svc,
        EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    await entitlements.RequireAsync(userId, PlanFeatures.Trips, ct);
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.UpdateTrip(tripId, req.Name, req.From, req.To, req.Destination, req.Icon);   // throws if missing / duplicate / end before start
        account.SetTripSavingCategory(tripId, req.SavingCategoryId);   // throws if the bucket isn't in this account
        account.SetTripCategory(tripId, req.CategoryId);               // throws if the category isn't in this account
        account.SetTripBudget(tripId, req.Budget);
        account.SetTripRate(tripId, req.SpendCurrency, req.Rate);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

// ⚠️ Deliberately NOT gated. Deleting is how a downgraded account tidies up, and locking the exit behind the
// subscription it just left would trap the data — the same reasoning that keeps the GETs and the detach free.
accounts.MapDelete("/{id:guid}/trips/{tripId:guid}", async (Guid id, Guid tripId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveTrip(tripId);   // detaches its expenses; the expenses themselves are untouched
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, tripId));
});

// Attach/detach one expense. Its own endpoint rather than a field on the expense edit — see EditExpenseRequest.
accounts.MapPut("/{id:guid}/expenses/{expenseId:guid}/trip", async (Guid id, Guid expenseId, SetExpenseTripRequest req, ClaimsPrincipal user, SnapshotService svc,
        EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    // The conditional gate. DETACHING is never gated — a downgrade must always be able to undo what it can no
    // longer do, or a wrong link becomes permanent. ATTACHING is free while the journey is still running (you can
    // keep logging a trip you are on right through to its end) and Pro once it is over, because filing a forgotten
    // booking against a finished trip is using the feature rather than finishing with it.
    // The plan is checked FIRST so a Pro account never pays for the snapshot read this needs.
    if (req.TripId is { } wantedTrip && !await entitlements.AllowsAsync(userId, PlanFeatures.Trips, ct))
    {
        // ⚠️ The finished-trip test must read the account that OWNS the trip. Looking a foreign trip up in this
        // account's snapshot finds nothing and silently skips the gate — a hole that opens the moment the trip
        // lives elsewhere, which is exactly what D1 introduced.
        var gateAccountId = req.TripAccountId is { } ga && ga != Guid.Empty ? ga : id;
        var snap = await svc.GetAsync(userId, gateAccountId, ct);
        var onDay = DateOnly.FromDateTime(DateTime.UtcNow);
        // A trip we cannot find is left to the mutation below to reject, with the error that actually names the
        // problem — 402 on a bad id would send the reader off after the wrong thing entirely.
        if (!string.IsNullOrEmpty(snap.Payload)
            && AccountSnapshotSerializer.Deserialize(snap.Payload).FindTrip(wantedTrip) is { } t
            && t.IsFinishedOn(onDay))
            await entitlements.RequireAsync(userId, PlanFeatures.Trips, ct);
    }

    // ★ Attaching to ANOTHER account's trip (D1). Two accounts change — this one gains the qualified link, the
    // other gains this account in the trip's SourceAccountIds directory — so it goes on the two-account spine,
    // which commits both or neither. The MONEY still moves nowhere: the expense stays in this account's period,
    // spending and budgets, because this account paid it. Only the other account's recap reaches across to gather
    // it, and it says where it came from when it does.
    if (req.TripAccountId is { } foreignAccountId && foreignAccountId != Guid.Empty && foreignAccountId != id
        && req.TripId is { } foreignTripId)
    {
        var (srcVersion, dstVersion, _) = await svc.MutateTwoAsync<object?>(userId, id, foreignAccountId, (source, dest) =>
        {
            // Same currency, and a hard gate rather than a display choice: Money's + throws on a mismatch, and
            // that sum feeds the destination's whole Trips screen server-side. Same rule as a settlement.
            if (!string.Equals(source.Currency, dest.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Both accounts must use the same currency.");
            var expense = source.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId)
                ?? throw new InvalidOperationException("That expense doesn't exist in this account.");
            var trip = dest.FindTrip(foreignTripId)
                ?? throw new InvalidOperationException("That trip doesn't exist in the other account.");
            expense.SetTrip(foreignTripId, foreignAccountId);
            trip.AddSourceAccount(id);   // idempotent, as MutateTwoAsync requires
            return null;
        }, ct);
        await notifier.AccountChangedAsync(id, userId, srcVersion);
        await notifier.AccountChangedAsync(foreignAccountId, userId, dstVersion);
        return Results.Ok(new MutationResultDto(srcVersion, expenseId));
    }

    // Detaching a row that was on a foreign trip has to tidy the other side's directory too — but only once
    // nothing here still points at that trip, or one detach would hide every other attachment from the recap.
    // Read before the mutation, so the single-account path below stays exactly what it was.
    Guid? tidyAccountId = null, tidyTripId = null;
    if (req.TripId is null)
    {
        var snap = await svc.GetAsync(userId, id, ct);
        if (!string.IsNullOrEmpty(snap.Payload)
            && AccountSnapshotSerializer.Deserialize(snap.Payload).Periods.SelectMany(p => p.Expenses)
                .FirstOrDefault(e => e.Id == expenseId) is { TripAccountId: { } wasAccount, TripId: { } wasTrip })
        {
            tidyAccountId = wasAccount; tidyTripId = wasTrip;
        }
    }

    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        // Any period, not just the open one: attaching last March's flight to this June's trip is the main reason
        // this endpoint exists, and that expense sits in a closed period.
        var expense = account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this account.");
        if (req.TripId is { } tripId && account.FindTrip(tripId) is null)
            throw new InvalidOperationException("That trip doesn't exist in this account.");
        expense.SetTrip(req.TripId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);

    if (tidyAccountId is { } tidyAcct && tidyTripId is { } tidyTrip)
    {
        // Best effort, and deliberately after the detach has already committed: a stale entry in the directory
        // costs one wasted snapshot read on the other account's next Trips view, while failing the user's detach
        // because the other account was busy would cost them the thing they actually asked for.
        try
        {
            var (_, tidyVersion, _) = await svc.MutateTwoAsync<object?>(userId, id, tidyAcct, (source, dest) =>
            {
                if (!source.ExpensesOnForeignTrip(tidyTrip, tidyAcct).Any())
                    dest.FindTrip(tidyTrip)?.RemoveSourceAccount(id);
                return null;
            }, ct);
            await notifier.AccountChangedAsync(tidyAcct, userId, tidyVersion);
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException or BadRequestException) { /* directory is a hint, not authority */ }
    }
    return Results.Ok(new MutationResultDto(version, expenseId));
});

// A fund's foreign currency + the rate it was bought at. See Fund.Currency for why the rate lives here and not on
// the trip, and SetFundCurrencyRequest for why this isn't two more fields on the fund edit.
accounts.MapPut("/{id:guid}/funds/{fundId:guid}/currency", async (Guid id, Guid fundId, SetFundCurrencyRequest req, ClaimsPrincipal user, SnapshotService svc,
        EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    // Holding money in a second currency is the Pro half of trips (MONETIZATION.md). Clearing it never is — a
    // downgrade must be able to put a wallet back to the account currency.
    if (!string.IsNullOrWhiteSpace(req.Currency))
        await entitlements.RequireAsync(userId, PlanFeatures.Trips, ct);
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.SetFundCurrency(fundId, req.Currency, req.Rate);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, fundId));
});

// Label one expense, in any period. Same shape and the same reason as the trip link above: a trip's bookings sit in
// months that are closed by the time the trip is being reviewed, and a tag moves no money.
accounts.MapPut("/{id:guid}/expenses/{expenseId:guid}/tag", async (Guid id, Guid expenseId, SetExpenseTagRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var expense = account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("That expense doesn't exist in this account.");
        if (req.TagId is { } tagId && account.FindTag(tagId) is null)
            throw new InvalidOperationException("That tag doesn't exist in this account.");
        expense.SetTag(req.TagId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, expenseId));
});

// Seed the trip labels once. Idempotent by design — the client sends its localized names, and a second call (or a
// second language) is a no-op, so the split can't fork into two parallel tag sets.
accounts.MapPost("/{id:guid}/trip-tags", async (Guid id, SeedTripTagsRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.EnsureTripTags(req.Tags.Select(t => (t.Name, t.Icon, t.CategoryId)));
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, id));
});

// Funds
accounts.MapPost("/{id:guid}/funds", async (Guid id, CreateFundRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, delta) = await svc.MutateAsync(userId, id, account =>
    {
        var fund = account.AddFund(req.Name, req.ParentId);   // validates parent + unique name
        account.SetFundNote(fund.Id, req.Note);
        account.SetFundIcon(fund.Id, req.Icon);
        return (fund.Id, WalletsMap.View(account, 0, bank.Balance, bank.BalanceCurrency));   // version stamped on the way out
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new FundMutationDto(version, delta.Id, delta.Item2 with { Version = version }));
});

accounts.MapPut("/{id:guid}/funds/{fundId:guid}", async (Guid id, Guid fundId, EditFundRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RenameFund(fundId, req.Name);   // throws if missing / duplicate name
        account.SetFundNote(fundId, req.Note);
        account.SetFundIcon(fundId, req.Icon);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, fundId));
});

accounts.MapPut("/{id:guid}/funds/{fundId:guid}/archived", async (Guid id, Guid fundId, SetArchivedRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.SetFundArchived(fundId, req.Archived);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, fundId));
});

// Optional ?moveOpeningBalancesTo={fundId} consolidates this fund's opening balances onto another (total-preserving)
// before removal; without it any balance is dropped with the fund. 400 on a removal blocker (sub-funds / last fund / refs).
accounts.MapDelete("/{id:guid}/funds/{fundId:guid}", async (Guid id, Guid fundId, Guid? moveOpeningBalancesTo, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveFund(fundId, moveOpeningBalancesTo);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, fundId));
});

// A fund's opening balance for the open period — what it held at the period's start (overwrites any existing opening).
accounts.MapPut("/{id:guid}/funds/{fundId:guid}/opening-balance", async (Guid id, Guid fundId, SetFundOpeningBalanceRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        if (account.FindFund(fundId) is null) throw new InvalidOperationException("That fund doesn't exist in this account.");
        period.SetInitialBalance(fundId, new Money(req.Amount, account.Currency));
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, fundId));
});

// Fund transfers — move money between two of the account's own funds within the open period (total-preserving, so the
// source may go negative; the domain caps only money leaving the account, which is a later cross-account slice).
// Mirrors BudgetingState.TransferFunds/EditFundTransfer/RemoveFundTransfer; synced sides are recorded (not moved).
accounts.MapPost("/{id:guid}/fund-transfers", async (Guid id, TransferFundsRequest req, ClaimsPrincipal user, SnapshotService svc, BankSyncService bankSvc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var date = req.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var bank = await bankSvc.GetStatusAsync(userId, id, ct);
    var (version, changed, delta) = await svc.MutateOrSkipAsync(userId, id, account =>
    {
        // ★ T0 — the retry check, before any validation (see the add-expense route).
        if (req.ClientId is { } key && key != Guid.Empty && account.CurrentPeriod is { } open &&
            open.FundTransfers.FirstOrDefault(t => t.ClientId == key) is { } already)
            return (false, (already.Id, WalletsMap.View(account, 0, bank.Balance, bank.BalanceCurrency)));
        var from = account.FindFund(req.FromFundId) ?? throw new InvalidOperationException("The source fund doesn't exist in this account.");
        var to = account.FindFund(req.ToFundId) ?? throw new InvalidOperationException("The destination fund doesn't exist in this account.");
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var transfer = period.TransferFunds(req.FromFundId, req.ToFundId, new Money(req.Amount, account.Currency), date, req.Note);
        transfer.SetSyncedSides(from.IsSynced, to.IsSynced);   // a synced side's real bank balance already reflects it
        transfer.SetClientId(req.ClientId is { } k && k != Guid.Empty ? k : null);
        return (true, (transfer.Id, WalletsMap.View(account, 0, bank.Balance, bank.BalanceCurrency)));
    }, ct);
    if (changed) await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new FundMutationDto(version, delta.Id, delta.Item2 with { Version = version }));
});

accounts.MapPut("/{id:guid}/fund-transfers/{transferId:guid}", async (Guid id, Guid transferId, EditFundTransferRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var from = account.FindFund(req.FromFundId) ?? throw new InvalidOperationException("The source fund doesn't exist in this account.");
        var to = account.FindFund(req.ToFundId) ?? throw new InvalidOperationException("The destination fund doesn't exist in this account.");
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var before = period.FundTransfers.FirstOrDefault(t => t.Id == transferId);
        var transfer = period.EditFundTransfer(transferId, req.FromFundId, req.ToFundId, new Money(req.Amount, account.Currency), req.Note);
        transfer.SetSyncedSides(from.IsSynced, to.IsSynced);
        transfer.SetBankLink(before?.BankExternalId, before?.AutoFiled ?? false);   // provenance AND badge survive — same rule as the expense edit
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, transferId));
});

accounts.MapDelete("/{id:guid}/fund-transfers/{transferId:guid}", async (Guid id, Guid transferId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        period.RemoveFundTransfer(transferId);   // 400 if the transfer isn't in this period
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, transferId));
});

// Contribution (income) categories
accounts.MapPost("/{id:guid}/contribution-categories", async (Guid id, CreateContributionCategoryRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, catId) = await svc.MutateAsync(userId, id, account =>
    {
        var category = account.AddContributionCategory(req.Name);   // validates unique name
        account.SetContributionCategoryIcon(category.Id, req.Icon);
        return category.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, catId));
});

accounts.MapPut("/{id:guid}/contribution-categories/{catId:guid}", async (Guid id, Guid catId, EditContributionCategoryRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RenameContributionCategory(catId, req.Name);   // throws if missing / duplicate name
        account.SetContributionCategoryIcon(catId, req.Icon);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, catId));
});

accounts.MapDelete("/{id:guid}/contribution-categories/{catId:guid}", async (Guid id, Guid catId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveContributionCategory(catId);   // 400 on a removal blocker (deposits reference it)
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, catId));
});

// D2: normalise "which account owns this bill's loan" to either null (this one — the ordinary case, single-account
// spine) or the other account's id. Guid.Empty and this account's own id both mean "here": the client says
// Guid.Empty to state it outright, and an older one says nothing at all.
static Guid? ForeignDebtAccount(Guid? debtAccountId, Guid billAccountId) =>
    debtAccountId is { } a && a != Guid.Empty && a != billAccountId ? a : null;

// --- Recurring items (bills / income expectations) -----------------------------------------------------------
// CRUD + pause/resume, plus the due-item handlers: confirm (posts a real expense/income with the actual amount, tunes
// a "typical" estimate, marks handled) and skip (marks handled, posts nothing). Posting goes through the shared
// Period.PostRecurring so it can't drift from the web. Kind/mode arrive as strings (RecurringMap → domain enums).
accounts.MapPost("/{id:guid}/recurring", async (Guid id, AddRecurringRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    // D2: a loan in ANOTHER account makes even CREATING the bill two-account — SyncLoanDueDay and
    // DefaultLoanToPaymentDriven both write to the bucket's side.
    var debtElsewhere = ForeignDebtAccount(req.LinkedDebtAccountId, id);

    // One body, run against (bill account, debt account) — the same pair on an ordinary bill. Written once so the
    // cross-account path cannot drift from the ordinary one; idempotent, as MutateTwoAsync requires.
    Guid Create(Account account, Account debtAccount)
    {
        if (debtElsewhere is not null) RecurringMap.ValidateSameCurrency(account, debtAccount);
        var kind = RecurringMap.Kind(req.Kind);
        RecurringMap.ValidateRefs(account, kind, req.CategoryId, req.FundId);
        var item = new RecurringItem(req.Name, kind, RecurringMap.Mode(req.Mode), req.Expected, req.DayOfMonth,
            req.CategoryId, req.FundId, req.Icon, req.AutoPost);
        item.SetCreatedOn(today);   // can't fall due before it existed
        RecurringMap.ValidateDebtLink(debtAccount, req.LinkedDebtBucketId);
        item.SetLinkedDebtBucket(req.LinkedDebtBucketId, debtElsewhere);
        RecurringMap.ValidateExcessCategory(account, req.ExcessCategoryId);   // the excess files HERE, where it is spent
        item.SetExcess(req.ExcessCategoryId, req.ExcessLabel);   // after the link — SetExcess self-clears without one
        RecurringMap.SyncLoanDueDay(debtAccount, item);   // a linked loan owns the due date
        // A brand-new bill is always a fresh link, so the loan starts following what gets logged here.
        RecurringMap.DefaultLoanToPaymentDriven(debtAccount, item, wasLinkedToSameBucket: false, today);
        account.AddRecurring(item);
        return item.Id;
    }

    long version;
    Guid recurringId;
    if (debtElsewhere is { } debtAccountId)
    {
        var (v, debtVersion, rid) = await svc.MutateTwoAsync(userId, id, debtAccountId, Create, ct);
        version = v; recurringId = rid;
        await notifier.AccountChangedAsync(debtAccountId, userId, debtVersion);
    }
    else
    {
        (version, recurringId) = await svc.MutateAsync(userId, id, a => Create(a, a), ct);
    }
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, recurringId));
});

accounts.MapPut("/{id:guid}/recurring/{recurringId:guid}", async (Guid id, Guid recurringId, UpdateRecurringRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ⚠️ A pre-read, because whether this is a two-account write depends on what is STORED: an older client that
    // has never heard of LinkedDebtAccountId re-sends the bucket id alone, and that must not silently re-home a
    // working cross-account link. See UpdateRecurringRequest.LinkedDebtAccountId for the rule.
    Guid? storedDebtAccount = null, storedBucket = null;
    if (await svc.GetAsync(userId, id, ct) is { Payload.Length: > 0 } preRead
        && AccountSnapshotSerializer.Deserialize(preRead.Payload).FindRecurring(recurringId) is { } stored)
    {
        storedDebtAccount = stored.LinkedDebtAccountId;
        storedBucket = stored.LinkedDebtBucketId;
    }
    var effectiveDebtAccount = req.LinkedDebtBucketId is not { } wantBucket || wantBucket == Guid.Empty
        ? null                                                        // unlinking: no loan, no second account
        : req.LinkedDebtAccountId is { } given
            ? (given == Guid.Empty ? null : given)                    // stated outright
            : (wantBucket == storedBucket ? storedDebtAccount : null); // unchanged bucket keeps its owner
    var debtElsewhere = ForeignDebtAccount(effectiveDebtAccount, id);

    object? Edit(Account account, Account debtAccount)
    {
        if (debtElsewhere is not null) RecurringMap.ValidateSameCurrency(account, debtAccount);
        var item = account.FindRecurring(recurringId) ?? throw new InvalidOperationException("That recurring item doesn't exist in this account.");
        // Captured BEFORE the link is overwritten: the payment-driven default fires on the transition into a link
        // only, so that re-saving a bill can't undo a user's deliberate switch back to schedule-driven.
        var previousLink = item.LinkedDebtBucketId;
        var previousOwner = item.LinkedDebtAccountId;
        RecurringMap.ValidateRefs(account, item.Kind, req.CategoryId, req.FundId);   // kind can't change on edit
        item.Update(req.Name, RecurringMap.Mode(req.Mode), req.Expected, req.DayOfMonth, req.CategoryId, req.FundId, req.Icon, req.AutoPost);
        RecurringMap.ValidateDebtLink(debtAccount, req.LinkedDebtBucketId);
        item.SetLinkedDebtBucket(req.LinkedDebtBucketId, debtElsewhere);   // authoritative: null unlinks
        // ⚠️ Deliberately NOT authoritative, unlike the line above. Absent leaves it as it was, Guid.Empty clears
        // it — see UpdateRecurringRequest.ExcessCategoryId. There is a live older Android client on this route,
        // and null-means-clear would have it wipe the excess configuration on every unrelated bill edit.
        if (req.ExcessCategoryId is { } reqExcess)
        {
            RecurringMap.ValidateExcessCategory(account, reqExcess);
            item.SetExcess(reqExcess == Guid.Empty ? null : reqExcess, req.ExcessLabel);
        }
        RecurringMap.SyncLoanDueDay(debtAccount, item);    // a linked loan owns the due date
        // "Same bucket" now means same bucket IN THE SAME ACCOUNT — moving a bill onto another household's loan is
        // a fresh link, and that loan should start following the payments logged here just as a new one would.
        // ★ The account the link moved AWAY from is deliberately left alone: DefaultLoanToPaymentDriven only ever
        // turns the setting ON, and un-setting it behind the user's back would be a third account to write and a
        // choice to unmake. That is what keeps this to two accounts.
        RecurringMap.DefaultLoanToPaymentDriven(debtAccount, item,
            previousLink == item.LinkedDebtBucketId && previousOwner == item.LinkedDebtAccountId, today);
        return null;
    }

    long version;
    if (debtElsewhere is { } editDebtAccountId)
    {
        var (v, debtVersion, _) = await svc.MutateTwoAsync(userId, id, editDebtAccountId, Edit, ct);
        version = v;
        await notifier.AccountChangedAsync(editDebtAccountId, userId, debtVersion);
    }
    else
    {
        (version, _) = await svc.MutateAsync(userId, id, a => Edit(a, a), ct);
    }
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, recurringId));
});

accounts.MapPut("/{id:guid}/recurring/{recurringId:guid}/active", async (Guid id, Guid recurringId, SetActiveRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        var item = account.FindRecurring(recurringId) ?? throw new InvalidOperationException("That recurring item doesn't exist in this account.");
        item.SetActive(req.Active);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, recurringId));
});

accounts.MapDelete("/{id:guid}/recurring/{recurringId:guid}", async (Guid id, Guid recurringId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, _) = await svc.MutateAsync<object?>(userId, id, account =>
    {
        account.RemoveRecurring(recurringId);
        return null;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, recurringId));
});

accounts.MapPost("/{id:guid}/recurring/{recurringId:guid}/confirm", async (Guid id, Guid recurringId, ConfirmRecurringRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();

    // ⚠️ D2 — this is the route that runs every month, and for a cross-account bill it is a genuine two-account
    // WRITE: expense rows land here, the loan's balance moves there. A pre-read decides which spine to use; it
    // costs one snapshot read on the ordinary path too, which is the price of the posting never drifting.
    Guid? debtElsewhere = null;
    if (await svc.GetAsync(userId, id, ct) is { Payload.Length: > 0 } confirmPre
        && AccountSnapshotSerializer.Deserialize(confirmPre.Payload).FindRecurring(recurringId) is { } confirmStored)
        debtElsewhere = ForeignDebtAccount(confirmStored.LinkedDebtAccountId, id);

    // ★ Returns the view AND whether the post had to degrade to a lump, so the caller can say so rather than
    // leaving a month booked as one lump while the loan quietly stalls.
    (RecurringViewDto View, bool Degraded) Confirm(Account account, Account debtAccount)
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var item = account.FindRecurring(recurringId) ?? throw new InvalidOperationException("That recurring item doesn't exist in this account.");
        if (req.ActualAmount > 0m) item.LearnFromActual(req.ActualAmount);   // tune a "typical" estimate toward reality
        var degraded = RecurringMap.Post(account, period, item, req.ActualAmount, userId, debtAccount);
        return (RecurringView.Of(account, 0), degraded);
    }

    long version;
    (RecurringViewDto View, bool Degraded) result;
    if (debtElsewhere is { } confirmDebtAccountId)
    {
        var (v, debtVersion, r) = await svc.MutateTwoAsync(userId, id, confirmDebtAccountId, Confirm, ct);
        version = v; result = r;
        await notifier.AccountChangedAsync(confirmDebtAccountId, userId, debtVersion);
    }
    else
    {
        (version, result) = await svc.MutateAsync(userId, id, a => Confirm(a, a), ct);
    }
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new RecurringMutationDto(version, recurringId, result.View with { Version = version },
        LoanUnreachable: result.Degraded));
});

accounts.MapPost("/{id:guid}/recurring/{recurringId:guid}/skip", async (Guid id, Guid recurringId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, view) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var item = account.FindRecurring(recurringId) ?? throw new InvalidOperationException("That recurring item doesn't exist in this account.");
        item.MarkHandled(period.From, skipped: true);   // handled for this period without posting anything
        return RecurringView.Of(account, 0);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new RecurringMutationDto(version, recurringId, view with { Version = version }));
});

// Undo a skip — the item falls due again in this period. The domain refuses if the item was posted rather than
// skipped, so this can never re-arm a bill whose expense is already on the ledger.
accounts.MapPost("/{id:guid}/recurring/{recurringId:guid}/unskip", async (Guid id, Guid recurringId, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, view) = await svc.MutateAsync(userId, id, account =>
    {
        var period = account.CurrentPeriod ?? throw new InvalidOperationException("There's no open period.");
        var item = account.FindRecurring(recurringId) ?? throw new InvalidOperationException("That recurring item doesn't exist in this account.");
        if (!item.SkippedIn(period.From))
            throw new InvalidOperationException("That item wasn't skipped in this period.");
        item.ClearHandled();
        return RecurringView.Of(account, 0);
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new RecurringMutationDto(version, recurringId, view with { Version = version }));
});

// Period lifecycle: roll into the next period (close current + open next, carrying opening balances), reschedule a
// period (later periods shift to stay contiguous), and undo the last period (re-opening the previous one). Mirrors
// BudgetingState.StartNextPeriod / ReschedulePeriod / RemoveLatestPeriod. Carry-over/reconciliation stays the caller's:
// the domain doesn't read live bank balances, so opening balances arrive in the request (as the web computes them).
accounts.MapPost("/{id:guid}/periods/start-next", async (Guid id, StartNextPeriodRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var today = req.Today ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var (version, periodId) = await svc.MutateAsync(userId, id, account =>
    {
        var previous = account.CurrentPeriod ?? throw new InvalidOperationException("There's no period to roll forward from.");
        // You can only roll into the next period once the current one has actually ended (mirrors CanStartNextPeriod).
        // This also makes a concurrency re-apply safe: if our start-next didn't win the save, the reloaded account still
        // has the old open period; a genuine double-submit against the freshly-opened (future-dated) period is rejected.
        if (previous.To >= today)
            throw new InvalidOperationException("The current period hasn't ended yet — you can only start the next period once it has.");
        previous.Close();

        var from = previous.To.AddDays(1);
        var to = from.AddMonths(1).AddDays(-1);
        var next = account.StartPeriod(from, to, req.CopyBudgets, req.AdjustBudgets && req.CopyBudgets);

        foreach (var f in account.RootFunds)
        {
            if (f.IsSynced)
            {
                // A synced fund's opening isn't hand-entered — the caller supplies the live bank balance captured at
                // rollover, stored informative-only so it doesn't move the money model. The server can't read the bank,
                // so when it's absent the synced fund simply carries no opening (same as the web with no live balance).
                if (req.SyncedFundClosingBalance is { } bal)
                    next.SetInitialBalance(f.Id, new Money(bal, account.Currency), informative: true);
                continue;
            }
            var opening = req.FundOpenings is not null && req.FundOpenings.TryGetValue(f.Id, out var v) ? v : 0m;
            next.SetInitialBalance(f.Id, new Money(opening, account.Currency));
        }
        return next.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, periodId));
});

accounts.MapPut("/{id:guid}/periods/{index:int}/schedule", async (Guid id, int index, ReschedulePeriodRequest req, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, periodId) = await svc.MutateAsync(userId, id, account =>
    {
        if (index < 0 || index >= account.Periods.Count)
            throw new InvalidOperationException("That period doesn't exist in this account.");
        var period = account.Periods[index];
        account.ReschedulePeriod(period, req.From, req.To);   // domain guards To >= From; shifts later periods
        return period.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, periodId));
});

accounts.MapDelete("/{id:guid}/periods/latest", async (Guid id, ClaimsPrincipal user, SnapshotService svc, SyncNotifier notifier, CancellationToken ct) =>
{
    var userId = user.UserId();
    var (version, periodId) = await svc.MutateAsync(userId, id, account =>
    {
        account.RemoveLatestPeriod();   // 400 if it's the only period; re-opens the now-latest period
        return account.CurrentPeriod!.Id;
    }, ct);
    await notifier.AccountChangedAsync(id, userId, version);
    return Results.Ok(new MutationResultDto(version, periodId));
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
    await svc.SetMappingAsync(user.UserId(), id, req.Description, req.Kind, req.TargetId, req.TagId, ct);
    return Results.NoContent();
});

accounts.MapDelete("/{id:guid}/bank/mappings", async (Guid id, string description, ClaimsPrincipal user, BankSyncService svc, CancellationToken ct) =>
{
    await svc.RemoveMappingAsync(user.UserId(), id, description, ct);
    return Results.NoContent();
});

// Public: the bank redirects here (with ?code=<auth code>&state=<accountId>[.n]) after the user consents. No
// auth — the code is exchanged with Enable Banking server-side to prove real consent — then we bounce back. A
// ".n" state suffix means the flow began in the native app, so the outcome routes through its deep link.
app.MapGet("/bank/callback", async (string? code, string? state, BankSyncService svc, CancellationToken ct) =>
{
    // Split the optional ".n" native marker off the account-id part of the state.
    var native = state?.EndsWith(".n", StringComparison.Ordinal) == true;
    var accountPart = native ? state![..^2] : state;
    string Done(string result) => native
        ? $"com.tandemtab.app://bank/callback?bank={result}"
        : $"/?bank={result}";

    if (!string.IsNullOrEmpty(code) && Guid.TryParseExact(accountPart, "N", out var accountId)
        && await svc.CompleteLinkAsync(accountId, code, ct))
        return Results.Redirect(Done("linked"));
    return Results.Redirect(Done("error"));
});

// --- Excel export (one sheet per period) ---------------------------------
accounts.MapGet("/{id:guid}/export", async (Guid id, ClaimsPrincipal user, AccountExportService svc, CancellationToken ct) =>
{
    var (bytes, fileName) = await svc.ExportAsync(user.UserId(), id, ct);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

// --- Invitations ---------------------------------------------------------
accounts.MapPost("/{id:guid}/invitations", async (Guid id, CreateInvitationRequest req, ClaimsPrincipal user, InvitationService svc, EntitlementService entitlements, SyncNotifier notifier, CancellationToken ct) =>
{
    // Sharing a household is the hero Pro feature and MONETIZATION.md's primary upgrade moment. Gate the invite
    // itself; inert while unlimited/pro. (Accept stays open — one Pro on the owner covers everyone on the account.)
    await entitlements.RequireAsync(user.UserId(), PlanFeatures.Share, ct);
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

// ── The assistant (R3) ────────────────────────────────────────────────────────────────────────────────────
// ⚠️ Read AssistantAskRequest before touching this: the question arriving here is MASKED by the client, which is
// the only place that holds the vocabulary to mask it. Nothing about the account is read on this path — the id in
// the route exists to scope the consent record and the gate, not to fetch anything — which is why there is no
// snapshot call and no membership check here. The reply is an intent key; the client does the rest.
accounts.MapPost("/{id:guid}/assistant/ask", async (
    Guid id, AssistantAskRequest req, ClaimsPrincipal user, AssistantService assistant,
    ConsentService consent, EntitlementService entitlements, CancellationToken ct) =>
{
    // Consent first, and per account: the assistant is opt-in and off by default, so an account that never turned
    // it on must not be able to reach the model even with a hand-written request.
    if (!await consent.IsActiveAsync(user.UserId(), id, ConsentService.Scope.Assistant, ct))
        return Results.Json(new { error = "The assistant is off for this account." }, statusCode: StatusCodes.Status403Forbidden);
    await entitlements.RequireAsync(user.UserId(), PlanFeatures.Assistant, ct);
    return Results.Ok(await assistant.AskAsync(user.UserId(), req, ct));
}).RequireRateLimiting("assistant");

// Whether the assistant can be offered at all on this deployment, and what is left of the caller's monthly
// budget. No key configured means no feature: the client hides the control rather than showing one that always
// fails. The remaining count is read here so a cap is something a person sees coming, not walks into.
accounts.MapGet("/assistant/status", async (ClaimsPrincipal user, AssistantService assistant, CancellationToken ct) =>
    Results.Ok(assistant.Available
        ? new AssistantStatusDto(true, await assistant.RemainingThisMonthAsync(user.UserId(), ct), assistant.MonthlyCap)
        : new AssistantStatusDto(false)));

app.MapHub<SyncHub>("/hubs/sync").RequireAuthorization();

// SPA fallback: any non-API route serves the WASM client's index.html (client-side routing).
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
});

app.Run();

// Resolve a thin surface read's optional ?period={index} to a Period. Null (absent or out-of-range) lets the
// map fall back to the account's current period — so an old/bad index degrades to "current" rather than 400ing.
static FinApp.Domain.Periods.Period? ResolvePeriod(FinApp.Domain.Accounts.Account account, int? index) =>
    index is { } i && i >= 0 && i < account.Periods.Count ? account.Periods[i] : null;

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
