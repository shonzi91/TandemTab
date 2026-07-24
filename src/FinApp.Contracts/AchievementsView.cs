namespace FinApp.Contracts;

// --- Path-B thin Achievements surface (docs/MOBILE.md) -------------------------------------------------------
// The thin counterpart of the thick app's Achievements modal. The full catalogue (earned + locked) is computed
// server-side by AchievementsService (moved to the domain in Session 42) — the same computation that drives the
// Home milestones counts, so the panel can't drift from the count. Earned dates come from the account's
// achievement log (stamped by the thick Dashboard); they're best-effort/decorative — nullable when unstamped.
// ⚠️ i18n: Title/Desc are the English strings baked by AchievementsService (identity translate). The whole thin
// dashboard is an English-only skeleton for now (its .razor labels are hardcoded too); localizing the thin UI —
// including mapping the stable Key to per-language copy — is a later, dashboard-wide concern.

/// <summary>One achievement. <see cref="Percent"/> is the locked-progress percent (null once earned);
/// <see cref="Tier"/> is "Bronze" | "Silver" | "Gold" (medal metal). <see cref="Key"/> is stable.</summary>
public record AchievementDto(
    string Key,
    string Icon,
    string Title,
    string Desc,
    bool Earned,
    int? Percent,
    string Tier,
    DateOnly? EarnedOn);

/// <summary>The thin Achievements surface: the full catalogue plus the same tallies the Home strip shows.</summary>
public record AchievementsViewDto(int Earned, int Total, int InProgress, IReadOnlyList<AchievementDto> Items)
{
    public static readonly AchievementsViewDto Empty = new(0, 0, 0, []);
}
