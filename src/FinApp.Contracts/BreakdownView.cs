namespace FinApp.Contracts;

// --- Path-B thin-client slice: the Breakdown ring (docs/MOBILE.md) ---------------------------------------------
// ⚠️ This read did not exist until Session 115. No server route stood behind the Breakdown at all, so every attempt
// to size it as client work was sizing the wrong thing — the rules below are the expensive half, and they live here.

/// <summary>One wedge of the ring. <see cref="Key"/> is the category/tag/fund id it groups, or one of the two
/// sentinels (<c>…00fe</c> "Everything else", <c>…00fd</c> "Transfers out").</summary>
public record BreakdownSliceDto(Guid Key, string Label, string? Icon, decimal Amount, string Color);

/// <summary>A bucket that received money in the window, so "Paid to goals" can name where it went instead of being
/// an unexplained total.</summary>
public record BreakdownPayoutDto(Guid BucketId, string Name, decimal Amount);

/// <summary>
/// The Breakdown: where the money went in a window, plus the four figures that reconcile to what the balance did.
///
/// <para>★★ <b>THE RING IS SPENDING.</b> Neither savings nor goal payouts are slices, and both exclusions are
/// deliberate and hard-won. Savings never left the account. Payouts did, but a pie is a <b>composition</b> chart —
/// it works only when the parts are the same kind of thing at comparable scale, and a €12,000 loan prepayment
/// beside €30 of groceries is neither. Measured when it was tried: the payout took 77.5% of the ring and squeezed
/// Food, the only slice a reader can act on, to 2.7%, while the total swung across three months so no two could be
/// compared. Where a payout's effect on the balance belongs is Trends, which already does it properly: a time axis
/// absorbs a one-off spike, a pie never can.</para>
///
/// <para>Nothing is hidden by that: <see cref="Income"/>, <see cref="Spent"/>, <see cref="SetAside"/> and
/// <see cref="PaidToGoals"/> are stated as figures, and <see cref="Spent"/> <b>equals</b> the sum of the slices.
/// Those two agreeing is the fix — they used to differ by the savings slice, silently, with nothing on screen
/// saying which question either was answering.</para>
///
/// <para>⚠️ <see cref="SetAside"/> <b>is never negative.</b> A signed version printed "−€3,320" beside a hero
/// reading €450 on the same screen. No drawdown is negative saving; money leaving buckets is a different fact with
/// its own slot, which is what <see cref="PaidToGoals"/> is.</para>
///
/// <para>⚠️ <see cref="Income"/> and <see cref="SetAside"/> take the period's <b>own</b> hero figures when the
/// window is exactly one period, rather than recomputing from dates. A look-alike recomputation is how two tiles
/// about one number come to disagree: a contribution dated inside the window but owned by another period counted
/// in one and not the other (observed at €5,421.77 against €5,344.38, with no way for the reader to tell which was
/// lying). Over a multi-period window there is no single period to take membership from, so the date-based sum is
/// the right tool there.</para>
/// </summary>
public record BreakdownViewDto(
    string Currency,
    DateOnly From,
    DateOnly To,
    /// <summary>"category" (default), "tag" or "fund" — what the slices group by.</summary>
    string GroupBy,
    decimal Income,
    decimal Spent,
    decimal SetAside,
    decimal PaidToGoals,
    IReadOnlyList<BreakdownSliceDto> Slices,
    IReadOnlyList<BreakdownPayoutDto> Payouts)
{
    public static readonly BreakdownViewDto Empty =
        new("", default, default, "category", 0m, 0m, 0m, 0m, [], []);
}
