namespace FinApp.Contracts;

/// <summary>
/// The Plans screen's data (OPEN-BETA P4). Monetization ships as <b>rails behind a flag</b>: when
/// <see cref="Enabled"/> is false (the default, and the state during beta) there is no plan UI at all and every
/// account is treated as fully entitled. When it's flipped on, this describes the tiers and the current plan so
/// the screen can render — no payment is wired yet.
/// <para><see cref="CurrentPlan"/> is "unlimited" while the flag is off, otherwise "pro" for grandfathered
/// beta-cohort accounts and "free" for everyone else. Prices are config values (a Cloud Run env var), so the
/// number shown is never hard-coded.</para>
/// </summary>
public sealed record PlansDto(
    bool Enabled,
    string CurrentPlan,
    bool IsBetaCohort,
    string Currency,
    string AnnualPrice,
    string MonthlyPrice);
