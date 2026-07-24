namespace FinApp.Contracts;

// --- Path-B thin Notifications (docs/MOBILE.md) --------------------------------------------------------------
// A read-only bell for the current period, computed server-side from the account. This is the DOMAIN-DERIVED
// SUBSET of the thick app's HomeNotifications: deficit, over-budget / near-cap categories, due recurring items,
// and "no income yet". Deliberately NOT ported: session-only notices (recurring auto-posted this session),
// dismissable nudges with client rotation, and the inline confirm/skip/reallocate ACTIONS (those writes live on
// their own tabs). Instead each item carries a TargetTab so the thin bell can navigate. Text is English (thin UI
// is an English-only skeleton for now — see the sibling *View DTOs). See NotificationsMap.

/// <summary>One bell item. <see cref="TargetTab"/> is the thin-dashboard tab that addresses it (null = no jump).</summary>
public record NotificationDto(string Icon, string Text, string? Desc, bool Urgent, string? TargetTab);

/// <summary>The thin notifications bell: the current-period alerts + the (urgent) tallies for the badge.</summary>
public record NotificationsViewDto(int Count, int UrgentCount, IReadOnlyList<NotificationDto> Items)
{
    public static readonly NotificationsViewDto Empty = new(0, 0, []);
}
