using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// Monetization ships as rails behind a flag (OPEN-BETA P4). The test host sets no <c>Monetization:Enabled</c>,
/// so the flag is off — which must mean no plan UI and no gating: every account reads "unlimited".
/// </summary>
public class MonetizationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public MonetizationApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task With_the_flag_off_the_user_is_unlimited_and_no_plan_ui_is_signalled()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("plan_user");

        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.NotNull(me);
        Assert.False(me!.MonetizationEnabled);
        Assert.Equal("unlimited", me.Plan);

        var plans = await client.GetFromJsonAsync<PlansDto>("/plans");
        Assert.NotNull(plans);
        Assert.False(plans!.Enabled);
        Assert.Equal("unlimited", plans.CurrentPlan);
    }

    /// <summary>
    /// The payment rails must be unreachable while the flag is off — not merely hidden in the UI. This is the
    /// property that makes "it kicks in when we lift the beta flag" safe: a hand-crafted request during beta
    /// can't start a checkout or hand itself a subscription.
    /// </summary>
    [Fact]
    public async Task Billing_endpoints_are_unreachable_while_monetization_is_off()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("plan_billing");

        var checkout = await client.PostAsJsonAsync("/billing/checkout", new CheckoutRequest(BillingInterval.Annual));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, checkout.StatusCode);

        var complete = await client.PostAsJsonAsync("/billing/sandbox/complete", new CheckoutRequest(BillingInterval.Annual));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, complete.StatusCode);

        // …and the plan is still unlimited afterwards: nothing was granted by the attempt.
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("unlimited", me!.Plan);
    }

    /// <summary>The landing page renders before anyone signs in, so its pricing feed must be anonymous — and
    /// must report Enabled=false during beta so the page shows no price list at all.</summary>
    [Fact]
    public async Task Public_pricing_is_anonymous_and_reports_the_flag_off()
    {
        var anon = _factory.CreateClient();

        var plans = await anon.GetFromJsonAsync<PlansDto>("/plans/public");
        Assert.NotNull(plans);
        Assert.False(plans!.Enabled);
        Assert.NotEmpty(plans.Features);                                   // the catalogue still describes the tiers
        Assert.Contains(plans.Features, f => f.Key == PlanFeatures.Share && !f.InFree && f.InPro);
    }

    /// <summary>
    /// The landing carousel needs BOTH consent and moderator approval. <c>/feedback</c> is anonymous, so a
    /// consented review posted by a stranger must NOT appear — approval defaults to 0 and only a deliberate
    /// promotion flips it. This test is the guard on that: it posts exactly the review an abuser would.
    /// </summary>
    [Fact]
    public async Task A_consented_but_unapproved_review_is_not_published()
    {
        var anon = _factory.CreateClient();

        var posted = await anon.PostAsJsonAsync("/feedback",
            new FeedbackRequest(5, "buy my thing at example.com", PublicConsent: true, Source: "landing"));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, posted.StatusCode);

        var reviews = await anon.GetFromJsonAsync<List<PublicReviewDto>>("/reviews/public");
        Assert.NotNull(reviews);
        Assert.DoesNotContain(reviews!, r => r.Comment.Contains("buy my thing"));
    }

    /// <summary>The gate helper itself: "unlimited" (the beta state) passes everything, Free passes only the
    /// free-tier capabilities, and an unknown key fails open rather than locking someone out.</summary>
    [Theory]
    [InlineData("unlimited", PlanFeatures.Share, true)]
    [InlineData("pro", PlanFeatures.Share, true)]
    [InlineData("free", PlanFeatures.Share, false)]
    [InlineData("free", PlanFeatures.Import, false)]
    [InlineData("free", PlanFeatures.Budgets, true)]
    [InlineData("free", PlanFeatures.Security, true)]
    [InlineData("free", "something-we-have-not-shipped-yet", true)]
    public void Allows_matches_the_documented_paywall_line(string plan, string feature, bool expected) =>
        Assert.Equal(expected, FinApp.Server.Auth.MonetizationService.Allows(plan, feature));
}
