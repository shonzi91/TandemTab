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
    public const string AdminEmail5 = "gate.admin5@example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // One address per test that needs to act as an admin — the host (and its user table) is shared across
        // the class, so re-using an address 409s in whichever test happens to run second.
        builder.UseSetting("Admin:Emails", $"{AdminEmail},{AdminEmail2},{AdminEmail3},{AdminEmail4},{AdminEmail5}");
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
    /// <summary>
    /// The wallet-currency gate, which is the same "never strand state" rule as the trips one above, applied to a
    /// setting rather than an action: <b>setting</b> a foreign currency is Pro, <b>clearing</b> it never is.
    /// <para>A lapsed subscriber whose holiday wallet is stuck in kronor has an account that converts every
    /// expense from it by a rate they can no longer reach — we would have broken their ledger to sell them
    /// something. The route says so in as many words; nothing pinned it until this, and both clients now build UI
    /// on the asymmetry (the phone keeps the fields visible on a downgraded plan when a currency is already set,
    /// precisely so the way back is not behind the paywall it is trying to leave).</para>
    /// </summary>
    [Fact]
    public async Task Free_cannot_set_a_wallets_currency_but_can_always_clear_one()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gatewallet", GatingServerFactory.AdminEmail5);
        var acct = (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Holiday", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        var agg = new Account("Holiday", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(Guid.NewGuid(), "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var cash = agg.FundId("Cash");
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        // Set while still unlimited — this is the wallet the downgrade will strand.
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/funds/{cash}/currency",
            new SetFundCurrencyRequest("SEK", 0.087m))).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();

        // Free may not set one — not even re-stating the rate on the wallet it already has.
        var blocked = await client.PutAsJsonAsync($"/accounts/{acct.Id}/funds/{cash}/currency",
            new SetFundCurrencyRequest("SEK", 0.09m));
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(PlanFeatures.Trips, (await blocked.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // But it may always put the wallet back to the account's own money.
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/funds/{cash}/currency",
            new SetFundCurrencyRequest(null, null))).EnsureSuccessStatusCode();

        var fund = (await client.GetFromJsonAsync<WalletsViewDto>($"/accounts/{acct.Id}/wallets"))!
            .Funds.Single(f => f.Id == cash);
        Assert.Null(fund.Currency);
        Assert.Null(fund.Rate);
    }

    /// <summary>
    /// The trips paywall (Session 106). The line is <b>starting a journey, not running one</b>: Free cannot create
    /// or edit a trip, but a trip it already has must be able to reach its end — read, start, finish (early too),
    /// log expenses against it while it runs, and delete it.
    /// <para>
    /// ★ The reasoning behind the open half is that <b>a paywall must never strand state</b>. A lapsed subscriber
    /// who cannot close a running trip is left with the app wearing trip mode indefinitely and dividing that
    /// trip's spend by a length nobody travelled — we would have broken their data to sell them something.
    /// </para>
    /// <para>
    /// The earlier rule allowed Free one <i>live</i> trip and 402'd only on the second, so a test asserting "the
    /// first POST succeeds on Free" would have passed both before and after. This pins the change from the other
    /// side: the FIRST create is refused.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Free_cannot_start_a_journey_but_can_always_finish_one()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gatetripper", GatingServerFactory.AdminEmail4);
        var acct = (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Travel", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        // A fresh account has no body; every trip mutation reads one, so seed a minimal aggregate first. The period
        // and the expense are what let the attach half of this test run at all.
        var agg = new Account("Travel", "EUR");
        agg.AddDefaultFunds();
        var member = Guid.NewGuid();
        agg.AddMember(member, "Me");
        var category = agg.AddCategory("Food").Id;
        var period = agg.StartPeriod(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(27)));
        period.Deposit(member, new FinApp.Domain.Common.Money(500m, "EUR"), fundId: agg.FundId("Bank"));
        var expense = period.AddExpense(new FinApp.Domain.Budgeting.Expense(
            category, new FinApp.Domain.Common.Money(20m, "EUR"), DateOnly.FromDateTime(DateTime.UtcNow), member, agg.FundId("Bank")));
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        // Two trips made while still unlimited: one running right now, one that finished last month.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var live = await client.PostAsJsonAsync($"/accounts/{acct.Id}/trips",
            new CreateTripRequest("Lisbon", today.AddDays(-1), today.AddDays(5)));
        live.EnsureSuccessStatusCode();
        var liveId = (await live.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var past = await client.PostAsJsonAsync($"/accounts/{acct.Id}/trips",
            new CreateTripRequest("Vienna", today.AddMonths(-2), today.AddMonths(-2).AddDays(4)));
        past.EnsureSuccessStatusCode();
        var pastId = (await past.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();

        // --- What Free may NOT do: begin a journey, or move one's dates ------------------------------------
        var blocked = await client.PostAsJsonAsync($"/accounts/{acct.Id}/trips",
            new CreateTripRequest("Oslo", today.AddMonths(2), today.AddMonths(2).AddDays(3)));
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(PlanFeatures.Trips, (await blocked.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // Editing is the gate that carries the weight — it is where the dates are.
        var edited = await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{liveId}",
            new EditTripRequest("Lisbon", today.AddDays(-1), today.AddDays(60), null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.PaymentRequired, edited.StatusCode);

        // Attaching to a journey that is already OVER is using the feature, not finishing with it.
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await client.PutAsJsonAsync($"/accounts/{acct.Id}/expenses/{expense.Id}/trip",
                new SetExpenseTripRequest(pastId))).StatusCode);

        // --- What Free may ALWAYS do: run the journey it already has to its end ----------------------------
        var view = await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{acct.Id}/trips");
        Assert.Contains(view!.Trips, t => t.Id == liveId);

        // Log against the running one…
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/expenses/{expense.Id}/trip",
            new SetExpenseTripRequest(liveId))).EnsureSuccessStatusCode();
        // …and unlink it again; the detach is never gated whatever the trip's state.
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/expenses/{expense.Id}/trip",
            new SetExpenseTripRequest(null))).EnsureSuccessStatusCode();

        // Confirm the departure, then end it EARLY — the whole point of leaving this door open.
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{liveId}/started", new StartTripRequest(true)))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{liveId}/finished", new FinishTripRequest(true)))
            .EnsureSuccessStatusCode();

        // The end date came IN to today. Finish can only ever shorten, so it is no route around the edit gate.
        var after = await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{acct.Id}/trips");
        var closed = Assert.Single(after!.Trips, t => t.Id == liveId);
        Assert.Equal(today, closed.To);

        // The undo is open too: finishing pulls a date in irreversibly, so it must not be a one-way door.
        (await client.PutAsJsonAsync($"/accounts/{acct.Id}/trips/{liveId}/finished", new FinishTripRequest(false)))
            .EnsureSuccessStatusCode();

        // And deleting — the exit is not behind the subscription you just left.
        (await client.DeleteAsync($"/accounts/{acct.Id}/trips/{liveId}")).EnsureSuccessStatusCode();
        var gone = await client.GetFromJsonAsync<TripsViewDto>($"/accounts/{acct.Id}/trips");
        Assert.DoesNotContain(gone!.Trips, t => t.Id == liveId);
    }
}
