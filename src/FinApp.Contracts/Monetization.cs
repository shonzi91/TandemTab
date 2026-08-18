namespace FinApp.Contracts;

/// <summary>
/// One capability, and which tiers include it. The server owns the <b>catalogue</b> (which features exist, and
/// where the paywall line falls) while the client owns the <b>wording</b> — hence a stable
/// <see cref="Key"/> rather than a display string, so the same list localizes into EN/BG without the server
/// knowing a language. <see cref="InFree"/> being true does not make the feature unimportant: the free tier is
/// deliberately a working product, and the profile screen lists what you <em>have</em>, not only what you lack.
/// </summary>
public sealed record PlanFeatureDto(string Key, bool InFree, bool InPro);

/// <summary>
/// The Plans screen's data (OPEN-BETA P4). Monetization ships as <b>rails behind a flag</b>: when
/// <see cref="Enabled"/> is false (the default, and the state during beta) there is no plan UI at all and every
/// account is treated as fully entitled. When it's flipped on, this describes the tiers and the current plan so
/// the screen can render.
/// <para><see cref="CurrentPlan"/> is "unlimited" while the flag is off, otherwise "pro" for grandfathered
/// beta-cohort accounts and paying subscribers, and "free" for everyone else. Prices are config values (a Cloud
/// Run env var), so no number is hard-coded.</para>
/// </summary>
public sealed record PlansDto(
    bool Enabled,
    string CurrentPlan,
    bool IsBetaCohort,
    string Currency,
    string AnnualPrice,
    string MonthlyPrice,
    IReadOnlyList<PlanFeatureDto> Features,
    /// <summary>Which payment provider is wired, and whether it is in sandbox. Surfaced so the UI can say plainly
    /// that no real money moves — a "checkout" that silently does nothing would be worse than one that says so.</summary>
    string PaymentProvider,
    bool PaymentSandbox);

/// <summary>How often the subscription bills. Annual is the headline price; monthly exists because asking for a
/// year up front is the single biggest conversion drop in this category.</summary>
public enum BillingInterval { Annual, Monthly }

/// <summary>Start a checkout for the Pro plan.</summary>
public sealed record CheckoutRequest(BillingInterval Interval);

/// <summary>
/// The result of starting a checkout. <see cref="RedirectUrl"/> is where the browser should go to pay; with the
/// sandbox provider it points back at our own simulated confirm page, so the whole upgrade path is walkable
/// end-to-end before a real provider account exists.
/// </summary>
public sealed record CheckoutSessionDto(string SessionId, string RedirectUrl, string Provider, bool Sandbox);

/// <summary>A public, consented, moderator-approved review shown on the landing page (OPEN-BETA P1).</summary>
public sealed record PublicReviewDto(int? Rating, string Comment, string At);

/// <summary>Free-beta capacity, shown on the landing page before anyone signs up. Anonymous — it has to render
/// for a stranger deciding whether to bother. <see cref="Remaining"/> is null when no cap is configured.</summary>
public sealed record BetaCapacityDto(bool Capped, int Cap, int Taken, int? Remaining, bool Full);

/// <summary>Admin-only: pin one account to a plan so the upgrade journey can be walked on demand. Plan is
/// "free", "pro", or null to clear.</summary>
public sealed record PlanOverrideRequest(string? Plan);

/// <summary>Admin-only: re-classify an account's cohort by email. "test" excludes it from the lifetime-Pro
/// allowance and the tester count; "beta" grandfathers it; "free" is an ordinary post-cap member.</summary>
public sealed record SetCohortRequest(string Email, string Cohort);

/// <summary>The outcome of a cohort change — echoes what the account now is, so the console can confirm rather
/// than assume.</summary>
public sealed record CohortResultDto(string Email, string Cohort, bool CountsAsBetaMember);

/// <summary>Admin-only view of a piece of feedback, for moderating what may appear on the landing page.</summary>
public sealed record AdminFeedbackDto(string Id, int? Rating, string? Comment, bool PublicConsent,
    bool Approved, string Source, string At);

/// <summary>Admin-only: approve or un-approve a review for public display.</summary>
public sealed record ApproveReviewRequest(bool Approved);

/// <summary>The known feature keys, shared so the client's label map and the server's catalogue can't drift.</summary>
public static class PlanFeatures
{
    public const string Budgets = "budgets";
    public const string Goals = "goals";
    public const string Export = "export";
    public const string Security = "security";
    public const string Share = "share";
    public const string Import = "import";
    public const string Debt = "debt";
    public const string Insights = "insights";
    public const string History = "history";
    public const string Caps = "caps";

    /// <summary>
    /// Trips. The line is <b>starting a journey, not running one</b> — Free cannot create a trip, edit one, fund it
    /// from a savings pot, or spend it in another currency.
    /// <para>
    /// ⚠️ What Free CAN always do, on a trip it already has: <b>read</b> it, <b>start</b> it, <b>finish</b> it
    /// (including early, and including the undo), <b>attach and detach expenses while it is still running</b>, and
    /// <b>delete</b> it. A journey somebody is on must be able to reach its end whatever happened to their
    /// subscription — otherwise a lapse leaves the app wearing trip mode forever and dividing the spend by a length
    /// nobody travelled. Attaching to a trip that is already <i>over</i> is Pro: that is using the feature rather
    /// than finishing with it.
    /// </para>
    /// <para>
    /// ★ <b>Editing is where the dates live</b>, and that is the gate that matters — Free must not be able to move
    /// a trip's window. Finishing early does pull the end date in, but it can only ever SHORTEN, never extend, so
    /// it is no route around the edit gate. See MONETIZATION.md.
    /// </para></summary>
    public const string Trips = "trips";
}
