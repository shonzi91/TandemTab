namespace FinApp.Contracts;

// --- Path-B thin-client period navigation (docs/MOBILE.md) --------------------------------------------------
// The thin Dashboard needs to let the user page between periods (months) the way the thick app does. This read
// lists every period with its dates + open/latest state so the client can render prev/next nav and a label, and
// the period-scoped surface reads (overview/spending/wallets/savings/budgets/recurring) accept an optional
// ?period={Index} to compute for a past period instead of the current one. Writes still target the open period
// (the domain posts there), so a thin client hides its write controls when the viewed period isn't the open one.

/// <summary>One period in the account's history. <see cref="Index"/> is its position in the oldest→newest list
/// (the selector the surface reads accept as <c>?period=</c>); <see cref="IsOpen"/> marks the still-editable period;
/// <see cref="IsLatest"/> marks the most recent (the only one that can start-next / be removed).</summary>
public record PeriodRowDto(int Index, DateOnly From, DateOnly To, bool IsOpen, bool IsLatest);

/// <summary>The account's periods, oldest→newest, plus the index of the current (latest) period — the thin
/// client's default view. Empty (CurrentIndex −1) when the account has no period yet.</summary>
public record PeriodsViewDto(string Currency, int CurrentIndex, IReadOnlyList<PeriodRowDto> Periods)
{
    public static readonly PeriodsViewDto Empty = new("", -1, []);
}
