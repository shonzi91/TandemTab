using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the thin Breakdown read (<see cref="BreakdownViewDto"/>) — the ring and the four figures beside it.
///
/// <para>A faithful port of the web's <c>BreakSlices</c> and the summary block above it. ⚠️ <b>Ported, not
/// redesigned.</b> The shape of this chart was argued out over three attempts and one revert; the rules that look
/// arbitrary are the ones that were paid for. They are written on <see cref="BreakdownViewDto"/> and repeated at
/// the lines that implement them.</para>
/// </summary>
public static class BreakdownMap
{
    // Long-tail grouping: past this many slices, keep the biggest (MaxSlices-1) and roll the rest into one grey
    // "Everything else" so the ring doesn't dissolve into unreadable slivers.
    private static readonly Guid OtherKey = new("00000000-0000-0000-0000-0000000000fe");
    private const int MaxSlices = 8;
    private const string OtherColor = "#9aa5b1";

    // Money sent to another account is outflow too, but it carries no category, tag or fund of its own — hence a
    // sentinel and a distinct slate colour.
    private static readonly Guid TransfersKey = new("00000000-0000-0000-0000-0000000000fd");
    private const string TransfersColor = "#7c8a99";

    private static readonly string[] SlicePalette =
    [
        "#2fb99a", "#4f8ff7", "#f0a63c", "#a06ff0", "#ef6f6f",
        "#3fb0c9", "#8fbf4a", "#e07ab0",
    ];

    private static string SliceColor(int i) => SlicePalette[i % SlicePalette.Length];

    /// <param name="from">Window start; defaults to the period's own start.</param>
    /// <param name="to">Window end; defaults to the period's own end.</param>
    /// <param name="groupBy">"category" (default), "tag" or "fund".</param>
    public static BreakdownViewDto View(Account account, Period? viewPeriod,
        DateOnly? from = null, DateOnly? to = null, string? groupBy = null)
    {
        var period = viewPeriod ?? account.CurrentPeriod;
        if (period is null) return BreakdownViewDto.Empty with { Currency = account.Currency };

        var wFrom = from ?? period.From;
        var wTo = to ?? period.To;
        // ★ "Is this window exactly the period?" is the question that decides whether the hero's own figures are
        // used or the dates are re-summed. It is not a rounding detail — see the contract's note.
        var isPeriod = wFrom == period.From && wTo == period.To;
        var mode = (groupBy ?? "category").ToLowerInvariant() switch
        {
            "tag" => "tag",
            "fund" => "fund",
            _ => "category",
        };

        var expenses = account.Periods.SelectMany(p => p.Expenses)
            .Where(e => e.Date >= wFrom && e.Date <= wTo).ToList();
        // ⚠️ AccountTransfersOut, not ExternalTransfers: a bucket payout is not spending, and including it here
        // would put the Breakdown's "Spent" at odds with the Home tile that already excludes it.
        var transfers = account.Periods.SelectMany(p => p.AccountTransfersOut)
            .Where(t => t.Date >= wFrom && t.Date <= wTo).ToList();

        // --- the four figures ------------------------------------------------------------------------------
        var overview = isPeriod ? AccountOverview.For(account, period) : (AccountOverview?)null;

        var income = overview is { } ovIncome
            ? ovIncome.MoneyIn.Amount
            : account.Periods.SelectMany(p => p.Contributions)
                .Where(c => c.MemberId != Period.CarryoverSource && c.Date >= wFrom && c.Date <= wTo)
                .Sum(c => c.Paid.Amount);

        // "Spent" comes from the RAW ROWS, not from the slices, so it is identical across the groupings — and it
        // is exactly the ring's total.
        var spent = expenses.Sum(e => Math.Abs(e.Amount.Amount))
            + transfers.Sum(t => Math.Abs(t.Amount.Amount));

        var setAside = overview is { } ovSaved ? ovSaved.SavedThisPeriod.Amount : SetAsideInRange(account, wFrom, wTo);

        var payouts = PayoutsByBucket(account, wFrom, wTo);

        // --- the slices ------------------------------------------------------------------------------------
        var groups = new Dictionary<Guid, decimal>();
        foreach (var e in expenses)
        {
            var amount = Math.Abs(e.Amount.Amount);
            if (amount == 0m) continue;
            switch (mode)
            {
                case "fund":
                    groups[e.FundId] = groups.GetValueOrDefault(e.FundId) + amount;
                    break;
                case "tag":
                    // An expense counts under EACH of its tags — untagged falls to the empty-guid bucket. That is
                    // why the tail rollup below is skipped for tags: one expense maps to several keys, so
                    // "everything else" would not be a well-defined set of rows to drill into.
                    if (e.TagIds.Count == 0) groups[Guid.Empty] = groups.GetValueOrDefault(Guid.Empty) + amount;
                    else foreach (var id in e.TagIds) groups[id] = groups.GetValueOrDefault(id) + amount;
                    break;
                default:
                    // The TOP-LEVEL category: a sub-category's spend belongs to its parent's wedge, or the ring
                    // splits one budget across several slivers nobody budgets by.
                    var root = account.FindCategory(e.CategoryId)?.ParentId ?? e.CategoryId;
                    groups[root] = groups.GetValueOrDefault(root) + amount;
                    break;
            }
        }

        string LabelFor(Guid k) => k == Guid.Empty
            ? (mode == "tag" ? "Untagged" : "Uncategorised")
            : mode == "fund" ? account.FindFund(k)?.Name ?? "—"
            : mode == "tag" ? account.FindTag(k)?.Name ?? "—"
            : account.FindCategory(k)?.Name ?? "—";
        string? IconFor(Guid k) => k == Guid.Empty ? null
            : mode == "fund" ? account.FindFund(k)?.Icon
            : mode == "tag" ? account.FindTag(k)?.Icon
            : account.FindCategory(k)?.Icon;

        var ordered = groups.OrderByDescending(kv => kv.Value).ToList();
        var slices = new List<BreakdownSliceDto>();
        var groupTail = ordered.Count > MaxSlices && mode != "tag";
        var head = groupTail ? ordered.Take(MaxSlices - 1).ToList() : ordered;
        for (var i = 0; i < head.Count; i++)
            slices.Add(new BreakdownSliceDto(head[i].Key, LabelFor(head[i].Key), IconFor(head[i].Key), head[i].Value, SliceColor(i)));
        if (groupTail)
            slices.Add(new BreakdownSliceDto(OtherKey, "Everything else", null,
                ordered.Skip(MaxSlices - 1).Sum(kv => kv.Value), OtherColor));

        // ★ RANKED IN, not appended. Appending sorted a transfer last however large it was, so for anyone moving
        // real money between accounts the single biggest outflow of the period sat at the bottom of a list ordered
        // by size. It keeps its own colour — it is not a category — but takes its rightful place.
        var xfer = transfers.Sum(t => Math.Abs(t.Amount.Amount));
        if (xfer > 0m) InsertByAmount(slices, new BreakdownSliceDto(TransfersKey, "Transfers out", null, xfer, TransfersColor));

        return new BreakdownViewDto(account.Currency, wFrom, wTo, mode,
            income, spent, setAside, payouts.Sum(p => p.Amount), slices, payouts);
    }

    /// <summary>
    /// What was paid out to each bucket in the window — "Paid to goals", named rather than left as a total.
    /// <para>⚠️ A payout is an <c>ExternalTransfer</c> that is NOT an account transfer (that set difference is what
    /// separates "money I sent to my other account" from "money I deployed at a goal"), and the payout row itself
    /// carries no bucket id — the paired <c>SavingAllocation</c> does. Every screen that skipped that join fell
    /// back to saying "a goal".</para>
    /// </summary>
    private static List<BreakdownPayoutDto> PayoutsByBucket(Account account, DateOnly from, DateOnly to)
    {
        var allocations = account.Periods.SelectMany(p => p.SavingAllocations).ToList();
        return account.Periods
            .SelectMany(p => p.ExternalTransfers.Except(p.AccountTransfersOut))
            .Where(t => t.Date >= from && t.Date <= to)
            .GroupBy(t => allocations.FirstOrDefault(a => a.SourceExternalTransferId == t.Id)?.SavingCategoryId ?? Guid.Empty)
            .Select(g => new BreakdownPayoutDto(
                g.Key,
                account.FindSavingCategory(g.Key)?.Name ?? "",
                g.Sum(t => Math.Abs(t.Amount.Amount))))
            .Where(p => p.Amount > 0m)
            .OrderByDescending(p => p.Amount)
            .ToList();
    }

    /// <summary>Put <paramref name="slice"/> where its size says it belongs — ahead of the first slice it outranks,
    /// but never below the "Everything else" tail, which is a rollup rather than a peer.</summary>
    private static void InsertByAmount(List<BreakdownSliceDto> slices, BreakdownSliceDto slice)
    {
        var at = slices.FindIndex(s => s.Key != OtherKey && s.Amount < slice.Amount);
        if (at < 0) at = slices.FindIndex(s => s.Key == OtherKey);
        if (at < 0) at = slices.Count;
        slices.Insert(at, slice);
    }

    /// <summary>
    /// What was <b>set aside</b> in a window that is not one period — the same "count the deposits" rule as
    /// <c>Period.SavingsSetAsideTotal</c> and the web's <c>SetAsideInRange</c>.
    /// <para>⚠️ Floored at zero on purpose. Nothing is un-saved when an earlier month's earmark reaches the thing
    /// it was for; the one drawdown that really is un-saving is money released back into a budget, which undoes the
    /// earmark, and that is the only negative counted.</para>
    /// </summary>
    private static decimal SetAsideInRange(Account account, DateOnly from, DateOnly to)
    {
        var allocations = account.Periods.SelectMany(p => p.SavingAllocations)
            .Where(a => a.Date >= from && a.Date <= to).ToList();
        // Bucket-to-bucket transfers are excluded on both halves: the same money wearing a different label.
        var deposits = allocations.Where(a => a.Amount.Amount > 0m && a.TransferPairId is null)
            .Sum(a => a.Amount.Amount);
        var released = allocations.Where(a => a.Amount.Amount < 0m && a.BudgetCategoryId is not null && a.TransferPairId is null)
            .Sum(a => -a.Amount.Amount);
        return Math.Max(0m, deposits - released);
    }
}
