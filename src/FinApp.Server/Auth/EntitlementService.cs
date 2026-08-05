using FinApp.Contracts;
using FinApp.Server.Infrastructure;

namespace FinApp.Server.Auth;

/// <summary>
/// The single source of truth for "what plan is this account on", and the server-side backstop for the Pro
/// paywall (OPEN-BETA P4). <c>/me</c>, <c>/plans</c> and every gated endpoint resolve through here so they can't
/// drift.
/// <para>
/// Resolution order (the logic <c>/me</c> long carried inline): an admin plan override wins over everything and
/// implies monetization is live <em>for that one account</em>; otherwise, while the global
/// <c>Monetization:Enabled</c> flag is off every account is <c>"unlimited"</c> — no gating at all, which is what
/// keeps the beta free and the gates inert. When the flag is on, beta-cohort accounts are grandfathered to
/// <c>"pro"</c> and paying subscribers are <c>"pro"</c>; everyone else is <c>"free"</c>.
/// </para>
/// </summary>
public sealed class EntitlementService(
    PlanOverrideService overrides,
    MonetizationService monetization,
    SignupService signups,
    SubscriptionService subscriptions)
{
    /// <summary>Everything <c>/me</c> and <c>/plans</c> need about an account's plan, resolved with the fewest
    /// reads. <see cref="GrandfatheredBeta"/> is true only when a beta-cohort account actually lands on Pro — so a
    /// tester pinned to Free doesn't get told "Pro is on us" over a Free plan.</summary>
    public sealed record Entitlement(string Plan, bool MonetizationLive, bool GrandfatheredBeta);

    public async Task<Entitlement> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var pinned = await overrides.GetAsync(userId, ct);
        var live = monetization.Enabled || pinned is not null;
        if (!live)
            return new Entitlement("unlimited", false, false);   // flag off, no override → no gating (beta default)

        var isBeta = await signups.IsBetaCohortAsync(userId, ct);
        var subscribed = await subscriptions.IsActiveAsync(userId, ct);
        var plan = pinned ?? monetization.PlanFor(isBeta, subscribed);
        return new Entitlement(plan, true, isBeta && plan == "pro");
    }

    /// <summary>The account's effective plan string alone. Convenience over <see cref="ResolveAsync"/>.</summary>
    public async Task<string> ResolvePlanAsync(Guid userId, CancellationToken ct = default) =>
        (await ResolveAsync(userId, ct)).Plan;

    /// <summary>
    /// Refuse a gated action when the account's plan doesn't include it — the server-side half of the paywall.
    /// A no-op for <c>"unlimited"</c>/<c>"pro"</c> and for unknown keys (fail open — a spurious 402 is worse than a
    /// missing one). The client gate gives the friendlier prompt first; this is the backstop a tampered or stale
    /// client can't skip. Throws <see cref="PaymentRequiredException"/> (HTTP 402) carrying the blocked key.
    /// </summary>
    public async Task RequireAsync(Guid userId, string featureKey, CancellationToken ct = default)
    {
        if (!MonetizationService.Allows(await ResolvePlanAsync(userId, ct), featureKey))
            throw new PaymentRequiredException(featureKey, "That's a Pro feature — upgrade to unlock it.");
    }
}
