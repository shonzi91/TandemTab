using FinApp.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace FinApp.Server.Tests;

/// <summary>
/// The lifetime-Pro allowance (OPEN-BETA, Session 87). The cap no longer blocks registration; it decides which
/// cohort — and therefore which tier — a new account lands on. These are pure-function tests over the two pieces
/// that make that decision: <see cref="BetaPolicy.CohortFor"/> and <see cref="MonetizationService.PlanFor"/>.
/// </summary>
public class BetaCohortTests
{
    private static BetaPolicy Policy(int cap, string? testPatterns = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Beta:Cap"] = cap.ToString(),
            ["Beta:TestEmailPatterns"] = testPatterns ?? "",
        }).Build());

    private static MonetizationService Money(bool enabled) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monetization:Enabled"] = enabled.ToString(),
        }).Build());

    [Fact]
    public void The_first_N_signups_are_beta_cohort_then_everyone_after_is_free()
    {
        var p = Policy(cap: 2);
        Assert.Equal(SignupService.BetaCohort, p.CohortFor("a@x.com", seatsTaken: 0));   // 1st lifetime seat
        Assert.Equal(SignupService.BetaCohort, p.CohortFor("b@x.com", seatsTaken: 1));   // 2nd (last)
        Assert.Equal(SignupService.FreeCohort, p.CohortFor("c@x.com", seatsTaken: 2));   // allowance full → Free
        Assert.Equal(SignupService.FreeCohort, p.CohortFor("d@x.com", seatsTaken: 99));  // still joins, still Free
    }

    [Fact]
    public void Our_own_test_addresses_never_take_a_lifetime_seat()
    {
        var p = Policy(cap: 2, testPatterns: "+test;@example.com");
        Assert.Equal(SignupService.TestCohort, p.CohortFor("me+test@gmail.com", seatsTaken: 0));
        Assert.Equal(SignupService.TestCohort, p.CohortFor("me@example.com", seatsTaken: 999));
    }

    [Fact]
    public void Grandfathering_happens_only_with_the_flag_on_and_only_for_the_beta_cohort()
    {
        Assert.Equal("unlimited", Money(false).PlanFor(isBetaCohort: true));               // flag off → no gating at all
        Assert.Equal("pro", Money(true).PlanFor(isBetaCohort: true));                       // lifetime seat → Pro
        Assert.Equal("free", Money(true).PlanFor(isBetaCohort: false));                     // post-cap → Free
        Assert.Equal("pro", Money(true).PlanFor(isBetaCohort: false, hasSubscription: true)); // paying → Pro
    }
}
