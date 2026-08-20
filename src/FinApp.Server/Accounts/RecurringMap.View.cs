using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin-Recurring read model (<see cref="RecurringViewDto"/>): every recurring bill/income
/// with its due state for the open period, computed server-side. (Named to sit alongside the existing
/// <see cref="RecurringMap"/> string-enum helper without clashing.)</summary>
public static class RecurringView
{
    public static RecurringViewDto Of(Account account, long version, Period? viewPeriod = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The editor's pickers travel with the view, so adding a bill needs no second read: spend categories for a
        // bill, contribution categories for income, funds, and the debts a bill can service. Built before the
        // no-period bail-out — an account between periods can still edit what recurs.
        var categories = account.Categories
            .Where(c => !c.IsArchived)
            .Select(c => new CategoryOptionDto(c.Id, c.Name, c.Icon, c.ParentId))
            .ToList();
        var contributionCategories = account.ContributionCategories
            .Select(cc => new CategoryOptionDto(cc.Id, cc.Name, cc.Icon, null))
            .ToList();
        var funds = account.RootFunds
            .Where(f => !f.IsArchived)
            .Select(f => new FundOptionDto(f.Id, f.Name, f.IsSynced))
            .ToList();
        var debts = account.SavingCategories
            .Where(s => s.IsDebt && !s.IsArchived)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new DebtOptionDto(s.Id, s.Name, s.DebtPaymentDriven))
            .ToList();

        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return RecurringViewDto.Empty with
            {
                Version = version, Currency = account.Currency,
                Categories = categories, ContributionCategories = contributionCategories, Funds = funds, Debts = debts,
            };

        var open = period.Status == PeriodStatus.Open;
        var items = account.RecurringItems.Select(r => new RecurringRowDto(
            r.Id,
            r.Name,
            r.Icon,
            r.Kind == RecurringKind.Income ? "income" : "expense",
            ModeString(r.AmountMode),
            r.ExpectedAmount,
            r.DayOfMonth,
            r.CategoryId,
            CategoryName(account, r),
            r.FundId,
            account.FundName(r.FundId),
            r.Active,
            open && r.IsDue(period.From, period.To, today),
            open && r.IsUpcoming(period.From, period.To, today, 5),
            open ? r.DaysUntilDue(period.From, period.To, today) : 0,
            r.HasKnownAmount,
            r.AutoPost,
            r.LinkedDebtBucketId,
            r.LinkedDebtBucketId is { } lb ? account.FindSavingCategory(lb)?.Name : null,
            open && r.SkippedIn(period.From),
            open && r.IsPending(period.From, period.To))).ToList();

        // Known bills still expected (unhandled) this period — the same rule as BudgetingState.BillsDueThisPeriod.
        var billsDue = open
            ? account.RecurringItems
                .Where(r => r.Kind == RecurringKind.Expense && r.HasKnownAmount && r.IsPending(period.From, period.To))
                .Sum(r => r.ExpectedAmount)
            : 0m;

        return new RecurringViewDto(version, account.Currency, billsDue, items,
            categories, contributionCategories, funds, debts);
    }

    private static string ModeString(RecurringAmountMode mode) => mode switch
    {
        RecurringAmountMode.Fixed => "fixed",
        RecurringAmountMode.Typical => "typical",
        _ => "reminder",
    };

    private static string CategoryName(Account account, RecurringItem r) =>
        (r.Kind == RecurringKind.Expense
            ? account.FindCategory(r.CategoryId)?.Name
            : account.FindContributionCategory(r.CategoryId)?.Name) ?? "—";
}
