using System.Net;
using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// The monetization counts. Worth pinning because their honest reading today is <b>zero</b> — no trial is
    /// modelled yet and every subscription row in existence is a sandbox one — and a query that is simply broken
    /// also returns zero. These assert the counters actually move when the rows they describe exist.
    /// </summary>
    [Fact]
    public async Task Trials_and_real_payments_are_counted_and_sandbox_rows_are_not()
    {
        var (_, trialUser) = await _factory.RegisterAndAuthAsync("metrics_trial");
        var (_, payingUser) = await _factory.RegisterAndAuthAsync("metrics_paying");
        var (_, sandboxUser) = await _factory.RegisterAndAuthAsync("metrics_sandbox");
        var (_, lapsedUser) = await _factory.RegisterAndAuthAsync("metrics_lapsed");

        using var scope = _factory.Services.CreateScope();
        var subs = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
        var metrics = scope.ServiceProvider.GetRequiredService<AdminMetricsService>();
        await subs.EnsureSchemaAsync();

        var before = await metrics.BuildAsync();

        // A trial is a row with Provider = 'trial'. One live, one already past its end.
        await subs.ActivateAsync(trialUser.UserId, "trial", "trial", null, sandbox: false, DateTimeOffset.UtcNow.AddDays(45));
        await subs.ActivateAsync(lapsedUser.UserId, "trial", "trial", null, sandbox: false, DateTimeOffset.UtcNow.AddDays(-1));
        // A real payment, and a sandbox one that must NOT be counted as revenue.
        await subs.ActivateAsync(payingUser.UserId, "annual", "stripe", "sub_x", sandbox: false, DateTimeOffset.UtcNow.AddYears(1));
        await subs.ActivateAsync(sandboxUser.UserId, "annual", "sandbox", "sb_x", sandbox: true, DateTimeOffset.UtcNow.AddYears(1));

        var after = await metrics.BuildAsync();

        // Started counts both trials — an expired trial was still taken up, which is the question being asked.
        Assert.Equal(before.TrialsStarted + 2, after.TrialsStarted);
        // Active counts only the one that hasn't run out.
        Assert.Equal(before.TrialsActive + 1, after.TrialsActive);
        // The real payment lands; the sandbox row and both trials stay out of it.
        Assert.Equal(before.PayingSubscribers + 1, after.PayingSubscribers);
    }
}
