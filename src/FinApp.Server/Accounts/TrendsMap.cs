using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the thin Trends read (<see cref="TrendsViewDto"/>) — one row per period, the month-by-month view of
/// money in, spent, kept, set aside and the balance left standing.
///
/// <para>★ <b>This is the aggregation, not a chart.</b> The web's <c>TrendRows()</c> walks
/// <c>State.Account.Periods</c> and totals each one; no thin contract carries a per-period total at all, so a
/// client without this would have to fetch every period's surface read separately and re-add them. That is not
/// porting a chart — it is re-implementing the sums in a second language, where "Spent" can quietly stop counting
/// out-transfers on one client and keep counting them on the other.</para>
///
/// <para>A faithful port of <c>TrendRows</c> and <c>TrendFocusValue</c>. The rules that look arbitrary are the
/// ones that were argued for; each is written on <see cref="TrendRowDto"/> and repeated at the line implementing
/// it.</para>
/// </summary>
public static class TrendsMap
{
    /// <param name="from">Window start, or null with <paramref name="to"/> for all time.</param>
    /// <param name="to">Window end, or null with <paramref name="from"/> for all time.</param>
    /// <param name="focus">"category" or "bucket" — what the <see cref="TrendRowDto.Focus"/> column narrows to.</param>
    /// <param name="focusId">The category or bucket that focus names.</param>
    public static TrendsViewDto View(Account account, DateOnly? from = null, DateOnly? to = null,
        string? focus = null, Guid focusId = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Periods.Count == 0) return TrendsViewDto.Empty with { Currency = account.Currency };

        // The focus is only honoured when it names something that exists — an id left over from a deleted category
        // narrows to nothing, and a chart of zeroes labelled with a name is worse than the un-narrowed one.
        var kind = (focus ?? "").ToLowerInvariant() switch
        {
            "category" when account.FindCategory(focusId) is not null => "category",
            "bucket" when account.FindSavingCategory(focusId) is not null => "bucket",
            _ => null,
        };
        var focusName = kind switch
        {
            "category" => account.FindCategory(focusId)?.Name,
            "bucket" => account.FindSavingCategory(focusId)?.Name,
            _ => null,
        };

        var rows = new List<TrendRowDto>();
        foreach (var p in account.Periods)
        {
            // ⚠️ A period that OVERLAPS the window is included whole — the window selects periods, it does not
            // slice them. All time (both bounds null) takes every period regardless of its dates, because the
            // web's "All" cannot lean on a date window either: that window anchors on the earliest activity date,
            // which can sit inside a later period once dates have been edited.
            if (from is { } f && to is { } t && (p.To < f || p.From > t)) continue;
            rows.Add(new TrendRowDto(
                p.From,
                p.To,
                decimal.Round(p.ContributionsPaidTotal.Amount, 2),
                // Expenses + transfers OUT to other accounts = all money out, so this agrees with the Home hero
                // and with the Breakdown, both of which count an out-transfer as money that left. Disbursements
                // are already excluded from AccountTransfersOutTotal — a bucket payout is not spending.
                decimal.Round(p.ExpensesTotal.Amount + p.AccountTransfersOutTotal.Amount, 2),
                // Floored at zero: a month whose sinking fund paid the bill it was filling is not a month of
                // negative saving. Money leaving buckets is a different fact, and DebtPaid below is its slot.
                decimal.Round(Math.Max(0m, p.SavingsNetTotal.Amount), 2),
                decimal.Round(p.ExpectedClosingBalance.Amount, 2),
                // Only disbursements onto a DEBT bucket. Deploying a holiday fund at a holiday is spending by
                // another route; deploying one at a loan is the balance falling because the debt did.
                decimal.Round(p.SavingAllocations
                    .Where(a => a.IsDisbursement && account.FindSavingCategory(a.SavingCategoryId) is { IsDebt: true })
                    .Sum(a => Math.Abs(a.Amount.Amount)), 2),
                FocusValue(account, p, kind, focusId)));
        }

        return new TrendsViewDto(account.Currency, from, to, kind, kind is null ? Guid.Empty : focusId, focusName, rows);
    }

    /// <summary>
    /// One period narrowed to the focused category or bucket. Zero when nothing is focused — the caller only reads
    /// it when something is.
    ///
    /// <para>★ <b>A categorised transfer out counts as spending here</b>, exactly as the budget rings and the
    /// Breakdown do. If it did not, the trend for "Food" would quietly disagree with the Food budget on the screen
    /// next door, and the reader would have no way to tell which one was lying.</para>
    ///
    /// <para>★ <b>A bucket's figure is its NET for the month, floored at zero</b> — the same rule the account-wide
    /// "Saved" uses, so focusing one bucket narrows the question without changing it.</para>
    /// </summary>
    private static decimal FocusValue(Account account, Period p, string? kind, Guid focusId)
    {
        if (kind == "category")
        {
            var spent = p.Expenses.Where(e => RootCategoryId(account, e.CategoryId) == focusId)
                            .Sum(e => Math.Abs(e.Amount.Amount))
                      + p.AccountTransfersOut.Where(t => t.CategoryId is { } c && RootCategoryId(account, c) == focusId)
                            .Sum(t => Math.Abs(t.Amount.Amount));
            return decimal.Round(spent, 2);
        }
        if (kind == "bucket")
        {
            var net = p.SavingAllocations.Where(a => a.SavingCategoryId == focusId).Sum(a => a.Amount.Amount);
            return decimal.Round(Math.Max(0m, net), 2);
        }
        return 0m;
    }

    /// <summary>A sub-category rolls up into its parent, so focusing "Food" includes "Takeaway". Mirrors
    /// <c>BudgetingState.RootCategoryId</c> and the same line inside <see cref="BreakdownMap"/>.</summary>
    private static Guid RootCategoryId(Account account, Guid categoryId) =>
        account.FindCategory(categoryId)?.ParentId ?? categoryId;
}
