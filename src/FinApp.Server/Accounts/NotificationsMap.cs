using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin notifications bell (<see cref="NotificationsViewDto"/>) — the domain-derived
/// subset of the thick <c>HomeNotifications</c>, for the account's <b>current</b> period: a savings deficit,
/// over-budget / near-cap categories, recurring items due, and "no income yet". Read-only; each item carries a
/// TargetTab the thin shell can jump to. Session-only notices, dismissable nudges and inline confirm/skip/reallocate
/// actions are deliberately out of scope (see NotificationsView.cs). Urgent items lead.</summary>
public static class NotificationsMap
{
    private static readonly BudgetCoverageService Coverage = new();

    public static NotificationsViewDto View(Account account)
    {
        if (account.CurrentPeriod is not { } period) return NotificationsViewDto.Empty;
        var today = DateOnly.FromDateTime(DateTime.Today);
        string Fmt(Money m) => MoneyText.Format(m.Amount, m.Currency);
        var items = new List<NotificationDto>();

        // Deficit — expenses ate into the savings earmark (the most urgent state).
        if (period.Deficit.Amount > 0m)
            items.Add(new NotificationDto("scale", $"Off balance — overspent by {Fmt(period.Deficit)}.",
                "Expenses ate into your savings earmark; it'll need covering next period.", true, "Wallets"));

        // Budgets: over-budget (urgent) or near its alert threshold but not yet over (info).
        foreach (var b in period.Budgets)
        {
            if (b.Allocated.Amount <= 0m) continue;
            var cov = Coverage.ForCategory(account, period, b.CategoryId);
            var name = account.FindCategory(b.CategoryId)?.Name ?? "A category";
            if (cov.IsOverBudget)
                items.Add(new NotificationDto("alert", $"{name} is over budget by {Fmt(cov.Spent - cov.Allocated)}.", null, true, "Budgets"));
            else if (cov.ThresholdReached)
                items.Add(new NotificationDto("info", $"{name} is at {cov.Percent ?? 0}% of its budget.", null, false, "Budgets"));
        }

        // Recurring items due this period — confirm them on the Recurring tab.
        foreach (var r in account.RecurringItems.Where(r => r.Active && r.IsDue(period.From, period.To, today)))
            items.Add(new NotificationDto(r.Icon ?? "repeat", $"{r.Name} is due.", null, false, "Recurring"));

        // Fresh period with no income logged yet.
        if (period.ContributionsPaidTotal.Amount <= 0m)
            items.Add(new NotificationDto("import", "No income added this period yet.",
                "Log it so your plan reflects real money.", false, "Income"));

        var ordered = items.OrderByDescending(i => i.Urgent).ToList();
        return new NotificationsViewDto(ordered.Count, ordered.Count(i => i.Urgent), ordered);
    }
}
