namespace FinApp.Contracts;

// --- Path-B thin Account settings (docs/MOBILE.md) ----------------------------------------------------------
// The editable per-account settings the thin dashboard surfaces: the display name (renamed via the existing
// PUT /accounts/{id}/name) and the savings-rate target (drives the Insights health score). The target lives in
// the snapshot (domain), so it rides the mutation spine via PUT /accounts/{id}/savings-target.

/// <summary>The thin account-settings read. <see cref="SavingsRateTarget"/> is a fraction 0..1 (the client shows
/// it as a percent).</summary>
public record AccountSettingsDto(string Name, string Currency, decimal SavingsRateTarget)
{
    public static readonly AccountSettingsDto Empty = new("", "", 0.20m);
}

/// <summary>Set the account's target savings rate. <see cref="Percent"/> is 0..100 (stored as a fraction).</summary>
public record SetSavingsTargetRequest(decimal Percent);
