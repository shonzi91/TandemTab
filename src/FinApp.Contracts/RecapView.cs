namespace FinApp.Contracts;

// --- Path-B thin-client slice: the week recap (docs/MOBILE.md, R2.5) -------------------------------------------
// ⚠️ The last of R2.5's three "server-read rows wearing Kotlin clothes", and the one that was NOT batched with
// Trends and the payoff plan. Same reason as those two: `WeeklyRecapService` walks EVERY period's expenses,
// savings allocations and contributions, and no thin contract carries a week-shaped total at all — the periods
// are months, and the recap's week routinely straddles two of them.
//
// ★ It is also the one where re-implementing in Kotlin would have been most tempting and worst. The service is
// ~40 lines of arithmetic and about eight rules that all look like details until one of them is missing: which
// week is covered, what counts as income, what counts as saving, and what "left over" is measured against. Each
// has a comment on it in WeeklyRecapService explaining what went wrong without it.

/// <summary>One label's share of the week — the same shape for the category and the tag breakdowns.
/// <see cref="Count"/> is how many expenses made it up, which is what separates one big buy from a habit.</summary>
public record RecapSliceDto(Guid Id, string Label, string? Icon, decimal Total, int Count);

/// <summary>The single largest expense of the week — the line most people can actually place in memory.
/// <see cref="Note"/> is usually the only thing that names the purchase: without it the row reads "Food, €80" and
/// the reader is left to remember which €80.</summary>
public record RecapBiggestDto(decimal Amount, Guid CategoryId, string CategoryName, string? CategoryIcon,
    DateOnly Date, string? Note);

/// <summary>
/// "Your week in money" for the <b>last completed</b> week, ready to render.
///
/// <para>★ <b>Every figure the card and the sheet print is a field here; the client does no arithmetic.</b> That
/// is deliberate and it is the whole point of this being a server read. <see cref="Change"/>, <see cref="Net"/>
/// and <see cref="EffectiveIncome"/> are each one line of subtraction — and each is a line whose *operands* were
/// argued over. A client that recomputes them is a client that can pick different operands, which is how the same
/// week comes to read differently on two screens with nothing on either saying which is lying.</para>
///
/// <para>⚠️ <b>The covered week is the last COMPLETED Monday–Sunday</b>, not the current one. A recap of a week
/// still running changes every time it is opened, and its comparison puts three days against seven — which reads
/// as a spending collapse every Tuesday.</para>
///
/// <para>⚠️ <see cref="EffectiveIncome"/> is what <see cref="Net"/> is measured against, and it is usually
/// <b>not</b> <see cref="Income"/>. Salary lands in one week a month, so the literal in-week income is zero three
/// weeks in four and "left over" would report a loss on every one of them. When the account has a basis for a
/// steady figure, that is used instead and <see cref="IncomeIsTypical"/> is true — which the client must surface,
/// because the tile then means "a typical week" rather than "money that arrived".</para>
///
/// <para><see cref="RoundUpsSaved"/> is a <b>subset</b> of <see cref="Saved"/>, never additional to it.</para>
/// </summary>
public record WeeklyRecapViewDto(
    string Currency,
    /// <summary>Monday of the covered week, inclusive.</summary>
    DateOnly From,
    /// <summary>Sunday of the covered week, inclusive.</summary>
    DateOnly To,
    decimal Spent,
    /// <summary>The week before — the comparison, not part of the headline.</summary>
    decimal PreviousSpent,
    /// <summary>Spent minus PreviousSpent. <b>Negative is the good direction.</b></summary>
    decimal Change,
    /// <summary>False when the week before had no spending. "100% less than last week" for someone's first week
    /// is noise wearing the costume of an insight, so the client draws no comparison at all.</summary>
    bool HasComparison,
    Guid? TopCategoryId,
    string? TopCategoryName,
    string? TopCategoryIcon,
    decimal TopCategorySpent,
    decimal Saved,
    /// <summary>The slice of <c>Saved</c> that arrived via round-ups — money set aside without a decision. Zero
    /// for the accounts that don't use them, which is most; drawn only when there is some, so it reads as a
    /// reward for using round-ups rather than a nag to switch them on.</summary>
    decimal RoundUpsSaved,
    /// <summary>How many separate expenses — the "how often did I reach for my card" figure a total alone hides.
    /// One €200 week and forty €5 days are different habits.</summary>
    int ExpenseCount,
    /// <summary>Money that literally arrived in the week. See <see cref="EffectiveIncome"/> before printing it.</summary>
    decimal Income,
    /// <summary>What <see cref="Net"/> is measured against — the steady weekly income when there is a basis for
    /// one, else the literal <see cref="Income"/>.</summary>
    decimal EffectiveIncome,
    /// <summary>True when <see cref="EffectiveIncome"/> is a smoothed typical week rather than money that landed.
    /// The client labels the tile differently for each; the same number under the wrong word is a false claim.</summary>
    bool IncomeIsTypical,
    /// <summary>What the week left behind: <see cref="EffectiveIncome"/> minus <see cref="Spent"/>. Positive is
    /// the good direction.</summary>
    decimal Net,
    RecapBiggestDto? Biggest,
    /// <summary>Every category touched, largest first.</summary>
    IReadOnlyList<RecapSliceDto> Categories,
    /// <summary>Every tag used, largest first. <b>Empty is the normal case, not a gap</b> — tagging is opt-in, so
    /// an empty "Tags" heading would read as a broken feature rather than an unused one.</summary>
    IReadOnlyList<RecapSliceDto> Tags,
    /// <summary>Nothing happened in either week, or the account has no periods at all. The client shows no card:
    /// a recap reporting zeroes is worse than no recap.</summary>
    bool IsEmpty)
{
    public static readonly WeeklyRecapViewDto Empty =
        new("", default, default, 0m, 0m, 0m, false, null, null, null, 0m, 0m, 0m, 0, 0m, 0m, false, 0m, null, [], [], true);
}
