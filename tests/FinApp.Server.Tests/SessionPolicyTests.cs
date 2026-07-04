using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinApp.Server.Tests;

/// <summary>
/// Sticky sessions: low-sensitivity users (no 2FA, no linked bank) get the long refresh window; enabling 2FA
/// steps them down to the strict window so the next token rotation shortens the session.
/// </summary>
public class SessionPolicyTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SessionPolicyTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Casual_user_gets_the_long_window_and_2fa_shortens_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("sess-anna", "sess-anna@example.com", "supersecret");
        using var scope = _factory.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<SessionPolicy>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;

        // No 2FA, no bank → the long "stay signed in" window.
        Assert.Equal(opts.RefreshTokenDaysRelaxed, await policy.RefreshDaysForAsync(auth.UserId));

        // Turning on 2FA makes the account sensitive → the strict window.
        var setup = (await (await client.PostAsync("/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        (await client.PostAsJsonAsync("/auth/2fa/confirm", new TwoFactorCodeRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();

        Assert.Equal(opts.RefreshTokenDays, await policy.RefreshDaysForAsync(auth.UserId));
    }
}
