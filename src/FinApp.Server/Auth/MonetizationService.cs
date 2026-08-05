using FinApp.Contracts;

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

    /// <summary>
    /// Where the paywall line falls, straight from MONETIZATION.md's Free-vs-Pro table. The free tier is a
    /// working product on purpose — solo, present-focused budgeting stays complete so people stay long enough to
    /// invite someone, which is the actual upgrade moment. Security and export are never gated.
    /// </summary>
    public static readonly IReadOnlyList<PlanFeatureDto> Catalogue = new[]
    {
        new PlanFeatureDto(PlanFeatures.Budgets,  InFree: true,  InPro: true),
        new PlanFeatureDto(PlanFeatures.Goals,    InFree: true,  InPro: true),
        new PlanFeatureDto(PlanFeatures.Export,   InFree: true,  InPro: true),
        new PlanFeatureDto(PlanFeatures.Security, InFree: true,  InPro: true),
        new PlanFeatureDto(PlanFeatures.Share,    InFree: false, InPro: true),
        new PlanFeatureDto(PlanFeatures.Import,   InFree: false, InPro: true),
        new PlanFeatureDto(PlanFeatures.Debt,     InFree: false, InPro: true),
        new PlanFeatureDto(PlanFeatures.Insights, InFree: false, InPro: true),
        new PlanFeatureDto(PlanFeatures.History,  InFree: false, InPro: true),
        new PlanFeatureDto(PlanFeatures.Caps,     InFree: false, InPro: true),
    };

    /// <summary>The plan an account is on. While the flag is off everyone is "unlimited" (no gating). When on,
    /// beta-cohort accounts are grandfathered to "pro" (the beta-tester promise) and paying subscribers are "pro";
    /// everyone else is "free".</summary>
    public string PlanFor(bool isBetaCohort, bool hasSubscription = false) =>
        !Enabled ? "unlimited" : (isBetaCohort || hasSubscription) ? "pro" : "free";

    /// <summary>Whether a plan may use a given feature key. "unlimited" (flag off) passes everything, which is
    /// what keeps the gates inert during beta — a gate added today changes nothing until the flag flips.</summary>
    public static bool Allows(string plan, string featureKey)
    {
        if (plan is "unlimited" or "pro") return true;
        var f = Catalogue.FirstOrDefault(x => x.Key == featureKey);
        return f is null || f.InFree;      // unknown key = not a gate; fail open rather than lock people out
    }

    /// <summary>How long a paid interval runs. Kept here so the sandbox and a future real provider agree.</summary>
    public static DateTimeOffset ExpiryFor(BillingInterval interval, DateTimeOffset from) =>
        interval == BillingInterval.Monthly ? from.AddMonths(1) : from.AddYears(1);
}
