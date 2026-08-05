namespace FinApp.Server.Auth;

/// <summary>
/// The monetization rails (OPEN-BETA P4). A single config flag decides whether any of it is live:
/// <c>Monetization:Enabled</c> (a Cloud Run env var, so flipping it is a revision update, no code deploy).
/// <para><b>Off by default, and off during beta.</b> While off there are no plans, no caps, and every account
/// is "unlimited" — exactly today's behaviour. Flip it on to test the plan surface; the standing decision is to
/// keep it off until after mobile + push (see MONETIZATION.md / docs/BILLING.md).</para>
/// <para>Prices come from config so no number is hard-coded — the hero annual price is <b>€29.99/yr</b>
/// (MONETIZATION.md; docs/BILLING.md's $39.99 is the stale one and should be reconciled to this).</para>
/// </summary>
public sealed class MonetizationService
{
    public bool Enabled { get; }
    public string Currency { get; }
    public string AnnualPrice { get; }
    public string MonthlyPrice { get; }

    public MonetizationService(IConfiguration config)
    {
        Enabled = config.GetValue("Monetization:Enabled", false);
        Currency = config["Monetization:Currency"] ?? "EUR";
        AnnualPrice = config["Monetization:AnnualPrice"] ?? "29.99";
        MonthlyPrice = config["Monetization:MonthlyPrice"] ?? "3.99";
    }

    /// <summary>The plan an account is on. While the flag is off everyone is "unlimited" (no gating). When on,
    /// beta-cohort accounts are grandfathered to "pro" (the beta-tester promise) and everyone else is "free".</summary>
    public string PlanFor(bool isBetaCohort) =>
        !Enabled ? "unlimited" : isBetaCohort ? "pro" : "free";
}
