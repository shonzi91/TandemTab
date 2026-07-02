using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

public class RefreshTokenTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RefreshTokenTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_and_login_return_a_refresh_token()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("refresh-alice");
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_yields_a_working_access_token()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("refresh-bob");
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var refreshed = (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;

        // A new access token and a rotated (different) refresh token come back.
        Assert.False(string.IsNullOrWhiteSpace(refreshed.Token));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));
        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);

        // The new access token actually authenticates.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.Token);
        var me = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Old_refresh_token_is_rejected_after_rotation()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("refresh-carol");
        var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Presenting the now-rotated (revoked) token must fail.
        var replay = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_revoked_token_revokes_the_whole_family()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("refresh-dave");
        var client = _factory.CreateClient();

        // Rotate once: original -> rotated1.
        var r1 = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        var rotated1 = (await r1.Content.ReadFromJsonAsync<AuthResponse>())!;

        // Replay the original (already revoked): breach — this nukes the whole family, including rotated1.
        var breach = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, breach.StatusCode);

        // rotated1 was valid a moment ago but must now be dead.
        var afterBreach = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(rotated1.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, afterBreach.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, auth) = await _factory.RegisterAndAuthAsync("refresh-erin");
        var client = _factory.CreateClient();

        var logout = await client.PostAsJsonAsync("/auth/logout", new LogoutRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(auth.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Malformed_refresh_token_is_rejected()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("not-a-real-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
