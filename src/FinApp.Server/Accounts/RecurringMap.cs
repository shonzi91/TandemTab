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
    /// schedule to split the payment against. Null (no link) is always fine.
    /// <para>⚠️ <paramref name="debtAccount"/> is the account that OWNS the bucket, which for a cross-account link
    /// (D2) is not the account holding the bill. Pass the right one or a valid foreign link reads as a broken
    /// same-account one.</para></summary>
    public static void ValidateDebtLink(Account debtAccount, Guid? bucketId)
    {
        if (bucketId is not { } id || id == Guid.Empty) return;
        if (debtAccount.FindSavingCategory(id) is not { IsDebt: true })
            throw new InvalidOperationException("That debt doesn't exist in this account.");
    }

    /// <summary>
    /// Both accounts must use the same currency before a bill in one can service a loan in the other (D2).
    /// <para>⚠️ A hard gate, not a display nicety: the installment rows are posted in the bill's currency and the
    /// principal is subtracted from a balance denominated in the loan's, so a mismatch would move a figure by a
    /// number that means something else. Same rule as a settlement or a cross-account trip.</para>
    /// </summary>
    public static void ValidateSameCurrency(Account billAccount, Account debtAccount)
    {
        if (!string.Equals(billAccount.Currency, debtAccount.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Both accounts must use the same currency.");
    }

    /// <summary>The excess line has to file somewhere real — a spend category that exists here. Null (never told)
    /// and <see cref="Guid.Empty"/> (explicitly cleared) are both fine; see
    /// <see cref="RecurringItem.ExcessCategoryId"/> for why "never told" is not an error.</summary>
    public static void ValidateExcessCategory(Account account, Guid? categoryId)
    {
        if (categoryId is not { } id || id == Guid.Empty) return;
        if (account.FindCategory(id) is null)
            throw new InvalidOperationException("That category doesn't exist in this account.");
    }

    /// <summary>
    /// One due date for a loan and the bill that services it. A loan's installment day is a contractual fact, so
    /// when the bucket states one it wins and the bill is moved onto it; when the bucket doesn't, the bill's day
    /// fills it in. Either way the two can no longer disagree — which they previously could, leaving the app
    /// claiming "due on the 30th" on the debt row and "day 15" on the bill that pays it.
    /// <para>Call after the link is set. No-op for an unlinked bill or a bucket that isn't a debt.</para>
    /// </summary>
    /// <param name="debtAccount">The account that owns the bucket — <b>not</b> the one holding the bill, on a
    /// cross-account link (D2). This writes to it, which is why linking is itself a two-account operation.</param>
    public static void SyncLoanDueDay(Account debtAccount, RecurringItem item)
    {
        if (item.LinkedDebtBucketId is not { } bucketId) return;
        if (debtAccount.FindSavingCategory(bucketId) is not { IsDebt: true } debt) return;

        if (debt.DebtInstallmentDay is { } loanDay)
            item.SetDayOfMonth(loanDay);
        else
            debtAccount.SetSavingDebtInstallmentDay(bucketId, item.DayOfMonth);
    }

    /// <summary>
    /// Linking a bill to a loan says "I pay this loan through this app", so the loan starts following the payments
    /// logged here rather than walking its own schedule. That is the whole signal the setting was asking for, and
    /// leaving it off meant a user could log every installment for months while the balance quietly ignored them.
    /// </summary>
    /// <remarks>
    /// <b>★ Only on the transition into a link (<paramref name="wasLinkedToSameBucket"/> false), never on every
    /// save.</b> The mode is still the user's to choose: someone who sets a linked loan back to "its own schedule"
    /// — the right call when the bill is a reminder for a payment that leaves an account this app can't see — must
    /// not have it flipped back the next time they rename the bill. Keying on the transition gets a sensible
    /// default without a second "has the user chosen?" flag to store and keep honest.
    /// <para>
    /// <see cref="Savings.SavingCategory.SetPaymentDriven"/> snapshots what is owed today and re-anchors there, so
    /// this changes what moves the balance from here on and never the figure itself.
    /// </para>
    /// </remarks>
    /// <param name="debtAccount">The account that owns the bucket — see <see cref="SyncLoanDueDay"/>.</param>
    public static void DefaultLoanToPaymentDriven(Account debtAccount, RecurringItem item, bool wasLinkedToSameBucket, DateOnly today)
    {
        if (wasLinkedToSameBucket) return;
        if (item.LinkedDebtBucketId is not { } bucketId) return;
        if (debtAccount.FindSavingCategory(bucketId) is not { IsDebt: true, DebtPaymentDriven: false }) return;
        debtAccount.SetSavingDebtPaymentDriven(bucketId, true, today);
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
    /// <param name="debtAccount">Where the loan lives. Defaults to <paramref name="account"/> — the ordinary case —
    /// and is the OTHER account on a cross-account link (D2), which only a two-account caller can supply.
    /// ⚠️ The tags stay in <paramref name="account"/>: the expense rows are posted there, so that is where a label
    /// on them has to exist.</param>
    /// <returns>True when a debt-linked bill had to post as a plain lump because its loan could not be reached —
    /// the caller must surface it. See <see cref="FinApp.Domain.Periods.Period.LastPostDegradedToLump"/>.</returns>
    public static bool Post(Account account, FinApp.Domain.Periods.Period period, RecurringItem item, decimal amount, Guid memberId,
        Account? debtAccount = null)
    {
        var fundSynced = account.FindFund(item.FundId)?.IsSynced ?? false;
        var owner = debtAccount ?? account;
        var debt = item.LinkedDebtBucketId is { } bucketId ? owner.FindSavingCategory(bucketId) : null;
        if (debt is not { IsDebt: true })
        {
            period.PostRecurring(item, amount, memberId, fundSynced);
            return period.LastPostDegradedToLump;
        }
        var (principalTag, interestTag) = account.EnsureInstallmentTags(debt.Id, "Loan principal", "Loan interest");
        period.PostRecurring(item, amount, memberId, fundSynced, debt, principalTag, interestTag);
        return period.LastPostDegradedToLump;
    }
}
