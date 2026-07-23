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
    public static RecurringViewDto Of(Account account, long version)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (account.CurrentPeriod is not { } period)
            return RecurringViewDto.Empty with { Version = version, Currency = account.Currency };

        var open = period.Status == PeriodStatus.Open;
        var items = account.RecurringItems.Select(r => new RecurringRowDto(
            r.Id,
            r.Name,
            r.Icon,
            r.Kind == RecurringKind.Income ? "income" : "expense",
            ModeString(r.AmountMode),
            r.ExpectedAmount,
            r.DayOfMonth,
            CategoryName(account, r),
            account.FundName(r.FundId),
            r.Active,
            open && r.IsDue(period.From, period.To, today),
            open && r.IsUpcoming(period.From, period.To, today, 5),
            open ? r.DaysUntilDue(period.From, period.To, today) : 0,
            r.HasKnownAmount)).ToList();

        // Known bills still expected (unhandled) this period — the same rule as BudgetingState.BillsDueThisPeriod.
        var billsDue = open
            ? account.RecurringItems
                .Where(r => r.Kind == RecurringKind.Expense && r.HasKnownAmount && r.IsPending(period.From, period.To))
                .Sum(r => r.ExpectedAmount)
            : 0m;

        return new RecurringViewDto(version, account.Currency, billsDue, items);
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
