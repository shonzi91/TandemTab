using FinApp.Domain.Accounts;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Accounts;

/// <summary>Maps the recurring-item request DTOs' language-independent string enums to the domain enums, and validates
/// that a recurring item's category/fund exist (the category is a spend category for an expense, a contribution
/// category for income). Thrown <see cref="InvalidOperationException"/>s surface as 400 through the mutation spine.</summary>
public static class RecurringMap
{
    public static RecurringKind Kind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "expense" or "bill" => RecurringKind.Expense,
        "income" => RecurringKind.Income,
        _ => throw new InvalidOperationException($"Unknown recurring kind “{kind}”."),
    };

    public static RecurringAmountMode Mode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "fixed" => RecurringAmountMode.Fixed,
        "typical" => RecurringAmountMode.Typical,
        "reminder" or "reminder-only" or "reminderonly" => RecurringAmountMode.ReminderOnly,
        _ => throw new InvalidOperationException($"Unknown recurring amount mode “{mode}”."),
    };

    public static void ValidateRefs(Account account, RecurringKind kind, Guid categoryId, Guid fundId)
    {
        if (account.FindFund(fundId) is null)
            throw new InvalidOperationException("That fund doesn't exist in this account.");
        var categoryOk = kind == RecurringKind.Expense
            ? account.FindCategory(categoryId) is not null
            : account.FindContributionCategory(categoryId) is not null;
        if (!categoryOk)
            throw new InvalidOperationException("That category doesn't exist in this account.");
    }
}
