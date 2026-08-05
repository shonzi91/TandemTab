using System.Net;

namespace FinApp.Server.Tests;

/// <summary>
/// The owner-only usage metrics gate (OPEN-BETA P2). The endpoint enumerates users, so it must fail closed: with
/// no admin allowlist configured (the test host sets none) nobody is an admin, and every caller is refused.
/// </summary>
public class AdminApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AdminApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_normal_signed_in_user_is_forbidden()
    {
        // Fails closed: authenticated, but not on the (empty) admin allowlist.
        var (client, _) = await _factory.RegisterAndAuthAsync("not_admin");
        var resp = await client.GetAsync("/admin/metrics");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
