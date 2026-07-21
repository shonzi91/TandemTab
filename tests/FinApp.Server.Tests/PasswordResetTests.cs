using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

public class PasswordResetTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public PasswordResetTests(FinAppServerFactory factory) => _factory = factory;

    // Stand in for the emailed link: issue a real token from the service the way the endpoint would.
    private async Task<string> IssueTokenAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PasswordResetService>().IssueTokenAsync(userId);
    }

    [Fact]
    public async Task Forgot_always_succeeds_even_for_an_unknown_identifier()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/auth/password/forgot", new ForgotPasswordRequest("nobody-here"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);   // never reveals whether the account exists
    }

    [Fact]
    public async Task A_valid_token_resets_the_password_and_old_one_stops_working()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("reset-amy", password: "oldpassword1");
        var token = await IssueTokenAsync(auth.UserId);

        var anon = _factory.CreateClient();
        var reset = await anon.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, "brandnewpass1"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var withNew = await anon.PostAsJsonAsync("/auth/login", new LoginRequest("reset-amy", "brandnewpass1"));
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);

        var withOld = await anon.PostAsJsonAsync("/auth/login", new LoginRequest("reset-amy", "oldpassword1"));
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    }

    [Fact]
    public async Task A_reset_token_works_only_once()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("reset-boris", password: "oldpassword1");
        var token = await IssueTokenAsync(auth.UserId);
        var anon = _factory.CreateClient();

        var first = await anon.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, "firstnewpass1"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await anon.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, "secondnewpass1"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task A_bogus_token_is_rejected()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest("not-a-real-token", "brandnewpass1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_too_short_password_is_rejected()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("reset-cora", password: "oldpassword1");
        var token = await IssueTokenAsync(auth.UserId);
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/auth/password/reset", new ResetPasswordRequest(token, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
