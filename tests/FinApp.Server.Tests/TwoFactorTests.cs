using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;

namespace FinApp.Server.Tests;

public class TwoFactorTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public TwoFactorTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<TwoFactorRecoveryDto> EnableAsync(HttpClient client)
    {
        var setup = (await (await client.PostAsync("/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        var confirm = await client.PostAsJsonAsync("/auth/2fa/confirm",
            new TwoFactorCodeRequest(Totp.Generate(setup.Secret)));
        confirm.EnsureSuccessStatusCode();
        return (await confirm.Content.ReadFromJsonAsync<TwoFactorRecoveryDto>())!;
    }

    [Fact]
    public async Task Enabling_2fa_reports_it_on_and_yields_recovery_codes()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tfa-anna");
        var recovery = await EnableAsync(client);

        Assert.Equal(10, recovery.RecoveryCodes.Length);
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.True(me!.TwoFactorEnabled);
    }

    [Fact]
    public async Task Confirm_with_a_wrong_code_is_rejected()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tfa-ben");
        await (await client.PostAsync("/auth/2fa/setup", null)).Content.ReadFromJsonAsync<TwoFactorSetupDto>();
        var confirm = await client.PostAsJsonAsync("/auth/2fa/confirm", new TwoFactorCodeRequest("000000"));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task Login_with_2fa_requires_a_ticket_then_a_code()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tfa-cora", "tfa-cora@example.com", "supersecret");
        var setup = (await (await client.PostAsync("/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        (await client.PostAsJsonAsync("/auth/2fa/confirm", new TwoFactorCodeRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        // Password login now returns a 2FA challenge, not tokens.
        var login = (await (await anon.PostAsJsonAsync("/auth/login", new LoginRequest("tfa-cora", "supersecret")))
            .Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.True(login.TwoFactorRequired);
        Assert.Null(login.Auth);
        Assert.False(string.IsNullOrWhiteSpace(login.TwoFactorTicket));

        // A wrong code doesn't burn the ticket...
        var wrong = await anon.PostAsJsonAsync("/auth/2fa", new TwoFactorLoginRequest(login.TwoFactorTicket!, "000000"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // ...the correct code completes login with real tokens.
        var ok = await anon.PostAsJsonAsync("/auth/2fa", new TwoFactorLoginRequest(login.TwoFactorTicket!, Totp.Generate(setup.Secret)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var auth = (await ok.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
    }

    [Fact]
    public async Task Recovery_code_completes_login_and_is_single_use()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tfa-dan", "tfa-dan@example.com", "supersecret");
        var recovery = await EnableAsync(client);
        var code = recovery.RecoveryCodes[0];

        var anon = _factory.CreateClient();
        var ticket1 = (await (await anon.PostAsJsonAsync("/auth/login", new LoginRequest("tfa-dan", "supersecret")))
            .Content.ReadFromJsonAsync<LoginResponse>())!.TwoFactorTicket!;
        var first = await anon.PostAsJsonAsync("/auth/2fa", new TwoFactorLoginRequest(ticket1, code));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The same recovery code can't be used a second time.
        var ticket2 = (await (await anon.PostAsJsonAsync("/auth/login", new LoginRequest("tfa-dan", "supersecret")))
            .Content.ReadFromJsonAsync<LoginResponse>())!.TwoFactorTicket!;
        var second = await anon.PostAsJsonAsync("/auth/2fa", new TwoFactorLoginRequest(ticket2, code));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Disabling_2fa_restores_direct_login()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tfa-evan", "tfa-evan@example.com", "supersecret");
        var setup = (await (await client.PostAsync("/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        (await client.PostAsJsonAsync("/auth/2fa/confirm", new TwoFactorCodeRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/auth/2fa/disable", new TwoFactorCodeRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        var login = (await (await anon.PostAsJsonAsync("/auth/login", new LoginRequest("tfa-evan", "supersecret")))
            .Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.False(login.TwoFactorRequired);
        Assert.NotNull(login.Auth);
    }
}
