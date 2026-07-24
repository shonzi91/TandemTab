namespace FinApp.Contracts;

// --- Path-B thin Onboarding checklist (docs/MOBILE.md) -------------------------------------------------------
// The thin counterpart of the thick Home "Getting started" card: four first-run steps whose Done state is derived
// server-side from the account (mirrors BudgetingState's TotalContributed / TotalBudgeted / AllExpenses /
// SavingBuckets), plus the account-level Dismissed flag. Read via GET /onboarding; dismissed via
// PUT /onboarding/dismissed (a former deferred whole-snapshot write, now a real command). Step Key is stable
// ("income" | "budget" | "expense" | "bucket"); Title/Desc are English (thin UI is an English-only skeleton).

/// <summary>One getting-started step. <see cref="Key"/> is stable; <see cref="Done"/> is derived from the account.</summary>
public record OnboardingStepDto(string Key, string Title, string Desc, bool Done);

/// <summary>The thin onboarding checklist: the account-level dismissed flag + the four steps.</summary>
public record OnboardingViewDto(bool Dismissed, IReadOnlyList<OnboardingStepDto> Steps)
{
    public static readonly OnboardingViewDto Empty = new(false, []);

    /// <summary>Every step done — the card should hide even before dismissal.</summary>
    public bool AllDone => Steps.Count > 0 && Steps.All(s => s.Done);
}
