using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// Hosts the real server against an isolated, temporary SQLite file (migrated on startup), so each test
/// class gets a clean database. Provides helpers to register users and obtain authenticated clients.
/// </summary>
public class FinAppServerFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"finapp-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Development disables the auth rate limiter (see Program.cs) so the suite's many rapid
        // registrations aren't throttled. Dev CORS is harmless here — tests aren't cross-origin.
        builder.UseSetting("environment", "Development");
        builder.UseSetting("ConnectionStrings:FinApp", $"Data Source={_dbPath}");
        // Keep tests hermetic: never inherit a developer's real provider credentials from user-secrets/env,
        // so "feature is off when unconfigured" assertions hold regardless of the machine running them.
        builder.UseSetting("BankSync:EnableBanking:ApplicationId", "");
        builder.UseSetting("BankSync:EnableBanking:PrivateKey", "");
        builder.UseSetting("Auth:Google:ClientId", "");
        builder.UseSetting("Auth:Google:ClientSecret", "");
        builder.UseSetting("Auth:Facebook:AppId", "");
        builder.UseSetting("Auth:Facebook:AppSecret", "");
        // Same reasoning: the dev appsettings turn these on for local testing, but the suite asserts the
        // off-by-default behaviour (admin fails closed; monetization is "unlimited"), so pin them off here.
        builder.UseSetting("Admin:Emails", "");
        builder.UseSetting("Monetization:Enabled", "false");
        // The assistant's experimental allowlist is an env var in production, so a developer who has it set would
        // otherwise turn every assistant test into "not enabled for this account". Empty means unrestricted, which
        // is the assumption the rest of the suite is written against; the two tests that want a list set their own.
        builder.UseSetting("Assistant:AllowedEmails", "");
        // The free-beta seat cap is REAL and would apply to the suite: nearly every test registers a user, and
        // the production default of 30 is well under the number this suite creates. Left alone, the suite would
        // start failing with "the free beta is full" the moment it grew past the cap — a confusing failure with
        // nothing to do with the test that happened to trip it. Raised, not disabled, so /beta/capacity still
        // reports a configured cap and the endpoint's shape stays under test.
        builder.UseSetting("Beta:Cap", "100000");
    }

    /// <summary>Register a new user and return a client with its bearer token attached.</summary>
    public async Task<(HttpClient Client, AuthResponse Auth)> RegisterAndAuthAsync(
        string username, string? email = null, string password = "password123")
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/register",
            new RegisterRequest(username, email ?? $"{username}@example.com", password));
        resp.EnsureSuccessStatusCode();
        var auth = (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return (client, auth);
    }

    /// <summary>Mark a user's email verified server-side (bank features require it; skips the email round-trip in tests).</summary>
    public async Task MarkEmailVerifiedAsync(Guid userId, string email)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EmailVerificationService>().MarkVerifiedAsync(userId, email);
    }

    /// <summary>A SignalR client wired through the in-memory test server (long polling), authenticated with the token.</summary>
    public HubConnection CreateHubConnection(string token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hubs/sync"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }
}
