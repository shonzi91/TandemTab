using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

public class EmailVerificationTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public EmailVerificationTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task New_password_account_starts_unverified()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("verify-amy");
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.False(me!.EmailVerified);
    }

    [Fact]
    public async Task Verify_link_with_a_bad_token_reports_failure()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/auth/verify-email?token=bogus");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/?emailVerified=0", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Resend_verification_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsync("/auth/resend-verification", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var (client, _) = await _factory.RegisterAndAuthAsync("verify-boris");
        var ok = await client.PostAsync("/auth/resend-verification", null);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }
}
