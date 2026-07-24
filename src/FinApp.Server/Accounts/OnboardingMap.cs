using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin onboarding checklist (<see cref="OnboardingViewDto"/>): the four first-run
/// steps with their Done state derived from the account, mirroring the thick Home card's conditions
/// (BudgetingState TotalContributed / TotalBudgeted / AllExpenses / SavingBuckets) so the two can't drift.
/// Income/budget are current-period; expense is any period (matches AllExpenses); bucket is any live saving
/// category. Account-level.</summary>
public static class OnboardingMap
{
    public static OnboardingViewDto View(Account account)
    {
        var period = account.CurrentPeriod;
        var incomeDone = period is not null && period.ContributionsPaidTotal.Amount > 0m;
        var budgetDone = period is not null && period.BudgetedTotal.Amount > 0m;
        var expenseDone = account.Periods.Any(p => p.Expenses.Count > 0);
        var bucketDone = account.SavingCategories.Any(s => !s.IsArchived);

        var steps = new List<OnboardingStepDto>
        {
            new("income",  "Add your income",       "Tell TandemTab what's coming in this month.",          incomeDone),
            new("budget",  "Set a budget",          "Cap a category so you know what's safe to spend.",     budgetDone),
            new("expense", "Log an expense",        "Record your first spend — or import a statement.",     expenseDone),
            new("bucket",  "Create a savings bucket","Set money aside — with or without a goal or debt.",   bucketDone),
        };

        return new OnboardingViewDto(account.OnboardingDismissed, steps);
    }
}
