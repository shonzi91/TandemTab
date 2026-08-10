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

    /// <summary>A bill can only be linked to a bucket that exists and is actually a debt — otherwise there'd be no
    /// schedule to split the payment against. Null (no link) is always fine.</summary>
    public static void ValidateDebtLink(Account account, Guid? bucketId)
    {
        if (bucketId is not { } id || id == Guid.Empty) return;
        if (account.FindSavingCategory(id) is not { IsDebt: true })
            throw new InvalidOperationException("That debt doesn't exist in this account.");
    }

    /// <summary>
    /// One due date for a loan and the bill that services it. A loan's installment day is a contractual fact, so
    /// when the bucket states one it wins and the bill is moved onto it; when the bucket doesn't, the bill's day
    /// fills it in. Either way the two can no longer disagree — which they previously could, leaving the app
    /// claiming "due on the 30th" on the debt row and "day 15" on the bill that pays it.
    /// <para>Call after the link is set. No-op for an unlinked bill or a bucket that isn't a debt.</para>
    /// </summary>
    public static void SyncLoanDueDay(Account account, RecurringItem item)
    {
        if (item.LinkedDebtBucketId is not { } bucketId) return;
        if (account.FindSavingCategory(bucketId) is not { IsDebt: true } debt) return;

        if (debt.DebtInstallmentDay is { } loanDay)
            item.SetDayOfMonth(loanDay);
        else
            account.SetSavingDebtInstallmentDay(bucketId, item.DayOfMonth);
    }

    /// <summary>
    /// Post a due recurring item, routing a debt-linked bill through the installment split. The single place both the
    /// confirm endpoint and auto-post go through, so a linked bill can't split one way when confirmed and another when
    /// posted automatically.
    /// <para>
    /// The English tag names here are only a <i>fallback</i>: <c>EnsureInstallmentTags</c> reuses whatever this loan's
    /// earlier rows already carry, so a user who first logged an installment in the Bulgarian UI keeps their own tags.
    /// </para>
    /// </summary>
    public static void Post(Account account, FinApp.Domain.Periods.Period period, RecurringItem item, decimal amount, Guid memberId)
    {
        var fundSynced = account.FindFund(item.FundId)?.IsSynced ?? false;
        var debt = item.LinkedDebtBucketId is { } bucketId ? account.FindSavingCategory(bucketId) : null;
        if (debt is not { IsDebt: true })
        {
            period.PostRecurring(item, amount, memberId, fundSynced);
            return;
        }
        var (principalTag, interestTag) = account.EnsureInstallmentTags(debt.Id, "Loan principal", "Loan interest");
        period.PostRecurring(item, amount, memberId, fundSynced, debt, principalTag, interestTag);
    }
}
