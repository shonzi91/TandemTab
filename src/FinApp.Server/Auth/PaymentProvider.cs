using FinApp.Contracts;

namespace FinApp.Server.Auth;

/// <summary>
/// The seam a real payment provider plugs into (OPEN-BETA P4, "prepare for integration"). Deliberately tiny: the
/// only thing the app needs from a provider is <i>"send this person somewhere to pay, and tell me when they
/// did"</i>. Entitlement itself lives in <see cref="SubscriptionService"/>, never in the provider — see the note
/// there about why a page render must not depend on a third party being up.
/// <para>
/// <b>Nothing here talks to Stripe yet, on purpose.</b> Wiring a live SDK would mean a secret key in a
/// <b>public</b> repo's config surface and API calls that cannot be exercised without a merchant account. The
/// sandbox implementation instead walks the entire flow — checkout → return → subscription row → plan flips to
/// Pro — so the UI, the gates and the tests are all provably correct before a provider is chosen. Adding
/// <c>StripePaymentProvider : IPaymentProvider</c> later is then a self-contained change with a known-good
/// contract on both sides.
/// </para>
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Provider name as shown to the user and stored on the subscription row ("sandbox", "stripe", …).</summary>
    string Name { get; }

    /// <summary>Whether this provider moves real money. The UI says so plainly when it doesn't.</summary>
    bool IsSandbox { get; }

    /// <summary>Begin an upgrade. Returns where to send the browser.</summary>
    Task<CheckoutSessionDto> CreateCheckoutAsync(Guid userId, BillingInterval interval, string returnUrl,
        CancellationToken ct = default);
}

/// <summary>
/// The default provider: simulates a hosted checkout by handing back a URL into our own confirm endpoint. Real
/// money never moves, and every subscription it writes is flagged <c>Sandbox = 1</c> so sandbox rows can be told
/// apart from (future) real ones in the same table — which matters the day a live provider is switched on and
/// somebody asks "who actually paid?".
/// </summary>
public sealed class SandboxPaymentProvider : IPaymentProvider
{
    public string Name => "sandbox";
    public bool IsSandbox => true;

    public Task<CheckoutSessionDto> CreateCheckoutAsync(Guid userId, BillingInterval interval, string returnUrl,
        CancellationToken ct = default)
    {
        // A real provider would create a hosted session here and return its URL. The session id is echoed back on
        // completion so the confirm step can't be replayed for a different interval than was started.
        var sessionId = $"sbx_{Guid.NewGuid():N}";
        var url = $"{returnUrl.TrimEnd('/')}/billing/sandbox?session={Uri.EscapeDataString(sessionId)}" +
                  $"&interval={interval}";
        return Task.FromResult(new CheckoutSessionDto(sessionId, url, Name, true));
    }
}

/// <summary>
/// Chooses the provider from config (<c>Payments:Provider</c>) and carries the switch-over rule: while
/// <c>Monetization:Enabled</c> is off, checkout is unreachable regardless of what is configured here. That single
/// dependency is what makes "it kicks in when we lift the beta flag" true rather than aspirational — there is no
/// second switch to remember.
/// <para>Config keys (all Cloud Run env vars, none in the repo — it is public):
/// <c>Payments__Provider</c> (default <c>sandbox</c>), <c>Payments__Mode</c> (<c>sandbox</c>|<c>live</c>).</para>
/// </summary>
public sealed class PaymentOptions(IConfiguration config)
{
    public string Provider { get; } = config["Payments:Provider"] ?? "sandbox";
    public bool Sandbox { get; } = !string.Equals(config["Payments:Mode"], "live", StringComparison.OrdinalIgnoreCase);
}
