namespace FinApp.Contracts;

// --- Path-B thin-client slice: Trends (docs/MOBILE.md, R2.5) --------------------------------------------------
// ⚠️ Sized as a SERVER slice, not as Kotlin, and the sweep that found it said why: Trends walks
// `State.Account.Periods` and totals each one, and no thin contract carries per-period totals at all. A client
// asked to draw this from the reads it already has would have to fetch every period's surface separately and
// re-add them — which is not "porting a chart", it is re-implementing the aggregation the web does over the whole
// aggregate, in a second language, where it can silently drift.

/// <summary>
/// One period as a row of the Trends charts. Dates rather than a label: month names are the client's job, and a
/// server-formatted one would be wrong in every locale but the server's.
///
/// <para>★ <see cref="Spent"/> is expenses <b>plus</b> money transferred out to other accounts, because both are
/// money that left. That is what makes this agree with the Home "Spent" tile and with the Breakdown, which count
/// an out-transfer the same way. (Disbursements — a bucket paying out to its goal — are already excluded from the
/// transfers-out total, since deploying a saving is not spending.)</para>
///
/// <para>★ <see cref="Saved"/> is the period's net saving <b>floored at zero</b>. A month in which a sinking fund
/// paid the bill it had been filling is not a month of negative saving; money leaving buckets is a different fact
/// with its own slot. The Breakdown's <c>SetAside</c> takes the identical line, for the identical reason.</para>
///
/// <para>★ <see cref="DebtPaid"/> counts only disbursements onto a <b>debt</b> bucket. Deploying a holiday fund at
/// a holiday is spending by another route; deploying one at a loan is the balance falling because the debt did.</para>
///
/// <para><see cref="Balance"/> is a <b>stock</b> (what stood at the end of the month) where every other figure
/// here is a <b>flow</b> (what moved during it). The web draws them on separate axes for that reason — a balance
/// an order of magnitude larger flattens the three lines actually being compared.</para>
/// </summary>
public record TrendRowDto(
    DateOnly From,
    DateOnly To,
    decimal Income,
    decimal Spent,
    decimal Saved,
    decimal Balance,
    decimal DebtPaid,
    /// <summary>The same month narrowed to the focused category or bucket, or 0 when nothing is focused. See
    /// <see cref="TrendsViewDto.FocusKind"/>.</summary>
    decimal Focus)
{
    /// <summary>What the month actually kept — the chart's headline question, which the older four-line version
    /// made the reader answer by eyeballing the gap between two wiggly lines.</summary>
    public decimal Net => Income - Spent;
}

/// <summary>
/// The Trends read: one row per period in the window, oldest→newest, plus the focus the rows were narrowed to.
///
/// <para>⚠️ <b>Rows are PERIODS, not calendar months, and the window selects them rather than slicing them.</b> A
/// period that overlaps the window at all is included whole. Cutting a period at the window's edge would print a
/// half-month beside full ones and invite exactly the comparison the chart exists to support.</para>
///
/// <para>Omitting both <c>from</c> and <c>to</c> means <b>all time</b> — every period regardless of its dates. The
/// web's "All" range cannot lean on a date window either, because that window anchors on the earliest activity
/// date, which can sit inside a later period when dates have been edited.</para>
/// </summary>
public record TrendsViewDto(
    string Currency,
    /// <summary>The window actually used, echoed back — the client sends a range and the server resolves the
    /// defaults, so this is what the rows were selected by. Both null on an all-time read.</summary>
    DateOnly? From,
    DateOnly? To,
    /// <summary>"category", "bucket", or null when the rows are the whole account.</summary>
    string? FocusKind,
    Guid FocusId,
    /// <summary>The focused thing's name, so the client can head the lower chart without a second lookup.</summary>
    string? FocusName,
    IReadOnlyList<TrendRowDto> Rows)
{
    public static readonly TrendsViewDto Empty = new("", null, null, null, Guid.Empty, null, []);
}
