namespace FinApp.Contracts;

/// <summary>
/// One trip as a thin client reads it: the stored fields, the state the app derives from <i>today</i>, and what the
/// journey has cost so far.
/// <para>
/// <b>★ The derived state travels, rather than each client re-deriving it.</b> Four states (upcoming → awaiting
/// start → active → finished) come out of three nullable dates plus a "today", and the rules are not obvious —
/// a finished trip is one the traveller declared over <i>or</i> whose last day has passed, and an active one is a
/// trip whose departure was <b>confirmed</b>, because a date is not a departure (see <c>Trip.StartedOn</c>). A
/// second implementation of that in Kotlin is a second place for it to drift, on the surface least able to prove
/// it. The client sends its own local date; the server answers with the state.
/// </para>
/// </summary>
/// <param name="State">One of <c>upcoming</c>, <c>awaiting-start</c>, <c>active</c>, <c>finished</c>.</param>
/// <param name="Day">Which day of the trip today is (1-based), or null when it isn't running.</param>
/// <param name="DaysUntil">Days until departure, or null once it has departed.</param>
/// <param name="Spent">Everything attached to the trip, whenever it was paid — not a date window.</param>
/// <param name="PrePaid">The part paid before departure: the half people forget.</param>
/// <param name="FundedFromSavings">The part drawn from the linked savings bucket as it was spent — distinct from
/// <paramref name="SavingsApplied"/>, which is money released into the trip's budget in advance.</param>
public record TripDto(
    Guid Id,
    string Name,
    string? Destination,
    DateOnly From,
    DateOnly To,
    string? Icon,
    Guid? SavingCategoryId,
    string? SavingCategoryName,
    decimal? Budget,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryIcon,
    string? SpendCurrency,
    decimal? Rate,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    decimal SavingsApplied,
    string State,
    int LengthInDays,
    int? Day,
    int? DaysUntil,
    decimal Spent,
    int ExpenseCount,
    decimal PrePaid,
    decimal OnTrip,
    decimal AfterReturn,
    decimal FundedFromSavings,
    decimal PerDay);

/// <summary>A trip label (Stay, Travel, Food &amp; drink…) as a picker option. <see cref="CategoryId"/> is the
/// category it files an expense into when picked, which is what makes the label a filing decision rather than a
/// note.</summary>
public record TripTagDto(Guid Id, string Name, string? Icon, Guid? CategoryId);

/// <summary>One wedge of a trip's spending — a category or a tag, already labelled and already ranked.</summary>
public record TripSliceDto(Guid Id, string Label, string? Icon, decimal Amount, int Count);

/// <summary>
/// One expense on a trip, gathered by <b>link</b> across every period — so a March flight appears under a June
/// trip. <see cref="When"/> says which side of the journey it falls on (<c>before</c> / <c>during</c> / <c>after</c>),
/// resolved here against the trip's dates so no client re-derives it. <see cref="TagName"/> is the trip's own axis
/// and leads the sub-line when set: on a trip that files into one category, printing the category on every row is
/// a column of the same word.
/// </summary>
public record TripExpenseRowDto(
    Guid Id,
    DateOnly Date,
    decimal Amount,
    string? Note,
    Guid CategoryId,
    string CategoryName,
    string? CategoryIcon,
    Guid? TagId,
    string? TagName,
    string? TagIcon,
    string When,
    // The clock, when the row carries one. Trailing-optional; null stays null — an untimed row shows no time
    // rather than a midnight nobody reported.
    TimeOnly? Time = null);

/// <summary>
/// One trip opened up: the card's own figures, the split behind them, and everything attached to it.
/// <para>
/// Its own read rather than fields on <see cref="TripsViewDto"/> — the list would otherwise carry every expense of
/// every journey the account has ever taken, to draw one card the reader may never open.
/// </para>
/// </summary>
/// <param name="SliceAxis"><c>tag</c> or <c>category</c>. The tag split is the headline whenever at least half the
/// trip is labelled; below that the pie would be mostly one unlabelled hole, so it falls back to the axis every
/// expense always carries. The decision travels so both clients lead with the same one.</param>
/// <param name="HasTagSlices">Whether any tag was used at all — the difference between "mostly unlabelled" and
/// "never labelled", which are two different sentences to show under the chart.</param>
public record TripDetailDto(
    TripDto Trip,
    IReadOnlyList<TripSliceDto> Slices,
    string SliceAxis,
    bool HasTagSlices,
    TripExpenseRowDto? Biggest,
    IReadOnlyList<TripExpenseRowDto> Expenses);

/// <summary>
/// The whole Trips surface in one read. <see cref="Trips"/> is newest departure first — the order the list reads in.
/// <para>
/// Until this existed the trip feature was <b>write-only over the API</b>: five commands and no way to see what
/// they had done, because the thick client reads trips out of the account snapshot it already carries. A thin
/// client cannot render what it cannot fetch, so Android had no trips at all — not a smaller trips, none.
/// </para>
/// </summary>
public record TripsViewDto(long Version, string Currency, IReadOnlyList<TripDto> Trips, IReadOnlyList<TripTagDto> TripTags)
{
    public static readonly TripsViewDto Empty = new(0, "", [], []);
}
