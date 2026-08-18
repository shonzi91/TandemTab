using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using Microsoft.AspNetCore.Hosting;

namespace FinApp.Server.Tests;

/// <summary>Test host with one admin address configured, so a test can pin its own account to Free/Pro through the
/// real admin override and exercise the server-side paywall exactly as production would (OPEN-BETA P4, Session 87).
/// The global monetization flag stays off — a plan override alone makes monetization live for that one account.</summary>
public sealed class GatingServerFactory : FinAppServerFactory
{
    public const string AdminEmail = "gate.admin@example.com";
    /// <summary>A second admin address: tests share this host, so each one that needs to pin a plan needs its own
    /// account, and an email can only be registered once.</summary>
    public const string AdminEmail2 = "gate.admin2@example.com";
    public const string AdminEmail3 = "gate.admin3@example.com";
    public const string AdminEmail4 = "gate.admin4@example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // One address per test that needs to act as an admin — the host (and its user table) is shared across
        // the class, so re-using an address 409s in whichever test happens to run second.
        builder.UseSetting("Admin:Emails", $"{AdminEmail},{AdminEmail2},{AdminEmail3},{AdminEmail4}");
    }
}

/// <summary>
/// The server-side half of the paywall. A Free plan must be refused at the endpoints that carry a real action —
/// sharing (invite), statement import, and the 2nd account (Free = 1). The refusal is a 402 naming the blocked
/// feature, so the client can raise the same upgrade prompt a local gate would. Pinning Pro lets the same calls
/// through. This is what makes "test Free/Pro" prod-like: the gates aren't cosmetic.
/// </summary>
public class GatingApiTests : IClassFixture<GatingServerFactory>
{
    private readonly GatingServerFactory _factory;
    public GatingApiTests(GatingServerFactory factory) => _factory = factory;

    private record GateError(string Error, string? Feature);

    [Fact]
    public async Task Free_is_refused_server_side_and_Pro_passes()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gateboss", GatingServerFactory.AdminEmail);

        // Pin Free — monetization goes live for this account only; plan resolves to "free".
        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.True(me!.MonetizationEnabled);
        Assert.Equal("free", me.Plan);
        Assert.False(me.ProBadge);   // no crown while testing Free, even though this account is beta-cohort

        // The 1st account is within the Free allowance…
        var first = await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Main", "EUR"));
        first.EnsureSuccessStatusCode();
        var acct = (await first.Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        // …the 2nd is not.
        var second = await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Second", "EUR"));
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Equal(PlanFeatures.Caps, (await second.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // Import and invite are Pro; the gate fires before any mutation, so an empty/irrelevant body still 402s.
        var import = await client.PostAsJsonAsync($"/accounts/{acct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.Equal(HttpStatusCode.PaymentRequired, import.StatusCode);
        Assert.Equal(PlanFeatures.Import, (await import.Content.ReadFromJsonAsync<GateError>())!.Feature);

        var invite = await client.PostAsJsonAsync($"/accounts/{acct.Id}/invitations", new CreateInvitationRequest("nobody"));
        Assert.Equal(HttpStatusCode.PaymentRequired, invite.StatusCode);
        Assert.Equal(PlanFeatures.Share, (await invite.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // Pin Pro — the same gated actions now pass (a 2nd account is created; import no longer 402s).
        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("pro"))).EnsureSuccessStatusCode();
        var mePro = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("pro", mePro!.Plan);
        Assert.True(mePro.ProBadge);   // crown on for Pro
        (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Second", "EUR"))).EnsureSuccessStatusCode();
        var importPro = await client.PostAsJsonAsync($"/accounts/{acct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.NotEqual(HttpStatusCode.PaymentRequired, importPro.StatusCode);
    }

    /// <summary>
    /// The cohort-correction endpoint: admin-only, validating, and effective. This is the safety net behind
    /// BetaPolicy's email patterns — an OAuth test sign-in can't use a +test alias, so some test accounts will
    /// land in the lifetime-Pro cohort and need moving out without hand-written SQL against production.
    /// </summary>
    [Fact]
    public async Task An_admin_can_move_an_account_out_of_the_lifetime_cohort()
    {
        var (admin, _) = await _factory.RegisterAndAuthAsync("cohortadmin", GatingServerFactory.AdminEmail3);
        var (victim, _) = await _factory.RegisterAndAuthAsync("cohortsubject", "cohort.subject@somewhere.test");

        // Fresh sign-up under the cap → lifetime cohort → unlimited + crowned while billing is off.
        var before = await victim.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("unlimited", before!.Plan);
        Assert.True(before.ProBadge);

        // Reclassify as one of ours.
        var res = await admin.PostAsJsonAsync("/admin/cohort",
            new SetCohortRequest("cohort.subject@somewhere.test", "test"));
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<CohortResultDto>())!;
        Assert.Equal("test", body.Cohort);
        Assert.False(body.CountsAsBetaMember);

        // It now gets the real Free experience: gated, no crown.
        var after = await victim.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("free", after!.Plan);
        Assert.False(after.ProBadge);

        // Guard rails: an invented cohort is refused (every downstream check reads this string), an unknown email
        // 404s rather than silently doing nothing, and a non-admin can't reach it at all.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/admin/cohort", new SetCohortRequest("cohort.subject@somewhere.test", "vip"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/admin/cohort", new SetCohortRequest("nobody@nowhere.test", "test"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await victim.PostAsJsonAsync("/admin/cohort", new SetCohortRequest("cohort.subject@somewhere.test", "beta"))).StatusCode);
    }

    /// <summary>
    /// A pinned account can actually complete a checkout. This is a regression test for a real defect: the
    /// billing endpoints checked the raw global <c>Monetization:Enabled</c> flag while <c>/me</c> reported
    /// monetization live for a pinned account — so the client (which trusts <c>/me</c>) showed "Upgrade to Pro"
    /// and the endpoint answered 404. The test switch could show the button but never exercise the flow it exists
    /// to rehearse. Both now resolve through <see cref="FinApp.Server.Auth.EntitlementService"/>.
    /// </summary>
    [Fact]
    public async Task A_pinned_account_can_walk_the_whole_sandbox_checkout()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gatebuyer", GatingServerFactory.AdminEmail2);

        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();
        Assert.Equal("free", (await client.GetFromJsonAsync<UserDto>("/me"))!.Plan);

        // Checkout must be REACHABLE (this is what 404'd before), and hand back a sandbox session.
        var checkout = await client.PostAsJsonAsync("/billing/checkout", new CheckoutRequest(BillingInterval.Annual));
        checkout.EnsureSuccessStatusCode();
        var session = (await checkout.Content.ReadFromJsonAsync<CheckoutSessionDto>())!;
        Assert.True(session.Sandbox);
        Assert.NotEmpty(session.SessionId);

        // Completing it lands on Pro. Note the pin is moved to "pro" rather than cleared: while the global flag is
        // off, plan resolution short-circuits before it consults subscriptions, so a cleared pin would drop the
        // account back to its cohort default and the purchase would be invisible.
        (await client.PostAsJsonAsync("/billing/sandbox/complete", new CheckoutRequest(BillingInterval.Annual)))
            .EnsureSuccessStatusCode();
        var after = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("pro", after!.Plan);
        Assert.True(after.ProBadge);
    }

    /// <summary>
    /// Trips are Pro in full (Session 106). The rule that matters is not just "Free can't make one" — it is the
    /// asymmetry: <b>creating</b> is refused, but <b>reading, detaching and deleting</b> stay open, so an account
    /// that lapses can still see everything it recorded, unlink a wrong attachment, and clean up. A gate that
    /// traps data is not a paywall, it is a hostage.
    /// <para>
    /// The earlier rule allowed Free one <i>live</i> trip and 402'd only on the second, so a test that merely
    /// asserted "the first POST succeeds on Free" would have passed both before and after. This one pins the
    /// change: the FIRST trip is refused.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Trips_are_Pro_to_create_but_never_to_read_detach_or_delete()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gatetripper", GatingServerFactory.AdminEmail4);
        var acct = (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Travel", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        // A fresh account has no body; every trip mutation reads one, so seed a minimal aggregate first.
        var agg = new Account("Travel", "EUR");
        agg.AddDefaultFunds();
        agg.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        // Make it while still unlimited, so there is a real trip to test the read/delete doors against.
        var from = new DateOnly(2026, 6, 1);
        var made = await client.PostAsJsonAsync($"/accounts/{acct.Id}/trips",
            new CreateTripRequest("Lisbon", from, from.AddDays(6)));
        made.EnsureSuccessStatusCode();
        var tripId = (await made.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();

        // The FIRST trip is refused now — this is the whole change.
        var blocked = await client.PostAsJsonAsync($"/accounts/{acct.Id}/trips",
            new CreateTripRequest("Vienna", from.AddMonths(2), from.AddMonths(2).AddDays(3)));
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(PlanFeatures.Trips, (await blocked.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // …and so are the other verbs that USE the feature.
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{tripId}/started", new StartTripRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{tripId}/finished", new FinishTripRequest(true))).StatusCode);

        // Reading never is. The journey stays visible to the account that recorded it.
        var view = await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{acct.Id}/trips");
        Assert.Contains(view!.Trips, t => t.Id == tripId);

        // Neither is deleting — the exit is not behind the subscription you just left.
        (await client.DeleteAsync($"/accounts/{acct.Id}/trips/{tripId}")).EnsureSuccessStatusCode();
        var gone = await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{acct.Id}/trips");
        Assert.DoesNotContain(gone!.Trips, t => t.Id == tripId);
    }
}
