using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the thin-client Trips read model (see <see cref="TripsViewDto"/>).
/// <para>
/// The cost figures are <see cref="TripRecapService"/>'s, not a second summation written for the wire: a trip's
/// total is gathered by the expenses' <i>trip link</i> across every period, and re-deriving that here would be a
/// parallel implementation of the one rule the feature rests on.
/// </para>
/// </summary>
public static class TripsMap
{
    /// <summary>Every trip in the account, newest departure first, with its state resolved against
    /// <paramref name="today"/> — the caller's own local date, since "is this trip running?" is a question about
    /// the traveller's day, not the server's.</summary>
    public static TripsViewDto View(Account account, long version, DateOnly today)
    {
        var recaps = new TripRecapService().BuildAll(account).ToDictionary(r => r.TripId);
        var trips = account.TripsByDeparture.Select(t =>
        {
            var r = recaps.GetValueOrDefault(t.Id);
            var category = t.CategoryId is { } cid ? account.FindCategory(cid) : null;
            var bucket = t.SavingCategoryId is { } sid ? account.FindSavingCategory(sid) : null;
            return new TripDto(
                t.Id, t.Name, t.Destination, t.From, t.To, t.Icon,
                t.SavingCategoryId, bucket?.Name,
                t.Budget, t.CategoryId, category?.Name, category?.Icon,
                t.SpendCurrency, t.Rate,
                t.StartedOn, t.FinishedOn, t.SavingsApplied,
                State(t, today),
                t.LengthInDays, t.DayOn(today), t.DaysUntil(today),
                r?.Spent.Amount ?? 0m, r?.ExpenseCount ?? 0,
                r?.PrePaid.Amount ?? 0m, r?.OnTrip.Amount ?? 0m, r?.AfterReturn.Amount ?? 0m,
                r?.FundedFromSavings.Amount ?? 0m, r?.PerDay.Amount ?? 0m);
        }).ToList();

        var tripTags = account.TripTags
            .Where(t => !t.IsArchived)
            .Select(t => new TripTagDto(t.Id, t.Name, t.Icon, t.CategoryId))
            .ToList();

        return new TripsViewDto(version, account.Currency, trips, tripTags);
    }

    /// <summary>
    /// One trip opened up: its card figures, the split behind them, and every expense linked to it.
    /// <para>
    /// ★ <b>Which axis the split uses is decided here, not by each client.</b> Tags lead whenever the trip is at
    /// least half labelled (<c>TagsAreRepresentative</c>); below that the ring would be mostly one unlabelled hole,
    /// so it falls back to categories — the axis every expense always carries. Two clients deciding that
    /// separately is two chances to lead with a different chart for the same trip.
    /// </para>
    /// </summary>
    public static TripDetailDto? Detail(Account account, long version, Guid tripId, DateOnly today)
    {
        if (account.FindTrip(tripId) is not { } trip) return null;
        var recap = new TripRecapService().Build(account, tripId);
        if (recap is null) return null;

        var byTag = recap.TagsAreRepresentative && recap.TagBreakdown.Count > 0;
        var slices = (byTag
                ? recap.TagBreakdown.Select(s => new TripSliceDto(s.Id, account.FindTag(s.Id)?.Name ?? "—", account.FindTag(s.Id)?.Icon, s.Total.Amount, s.Count))
                : recap.CategoryBreakdown.Select(s => new TripSliceDto(s.Id, account.FindCategory(s.Id)?.Name ?? "—", account.FindCategory(s.Id)?.Icon, s.Total.Amount, s.Count)))
            .Where(s => s.Amount > 0m)
            .ToList();

        // Newest first, and newest on the clock within a day (owner's call). A trip is the one list where a single
        // day holds a dozen entries, and a size order turns a journey into a leaderboard — you read a trip as the
        // days you lived, so the ledger runs backwards through them. `SortTime` puts an untimed row at the bottom
        // of its own day rather than treating it as midnight; the id breaks the last tie so two renders of the same
        // data cannot reshuffle.
        var rows = account.TripExpenses(tripId)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.SortTime).ThenBy(e => e.Id)
            .Select(e => Row(account, trip, e))
            .ToList();

        // ⚠️ Computed on its own now, not taken as rows[0]. It used to be the head of an amount-sorted list, so
        // re-ordering the ledger by date would silently have made "biggest single thing" mean "most recent".
        var biggest = account.TripExpenses(tripId)
            .OrderByDescending(e => Math.Abs(e.Amount.Amount)).ThenByDescending(e => e.Date).ThenBy(e => e.Id)
            .Select(e => Row(account, trip, e))
            .FirstOrDefault();
        return new TripDetailDto(
            View(account, version, today).Trips.First(t => t.Id == tripId),
            slices, byTag ? "tag" : "category", recap.TagBreakdown.Count > 0, biggest, rows);
    }

    private static TripExpenseRowDto Row(Account account, FinApp.Domain.Budgeting.Trip trip, FinApp.Domain.Budgeting.Expense e)
    {
        var category = account.FindCategory(e.CategoryId);
        var tag = e.TagId is { } tid ? account.FindTag(tid) : null;
        return new TripExpenseRowDto(
            e.Id, e.Date, Math.Abs(e.Amount.Amount), e.Note,
            e.CategoryId, category?.Name ?? "—", category?.Icon,
            tag?.Id, tag?.Name, tag?.Icon,
            e.Date < trip.From ? "before" : e.Date > trip.To ? "after" : "during",
            e.Time);
    }

    /// <summary>
    /// The four states, in the order they must be tested. <b>Finished is checked first</b> — a trip declared over
    /// is over even if today still falls inside its dates, which is the whole reason Finish exists as its own
    /// action. <b>Active requires a confirmed departure</b>: between the start date and that confirmation the trip
    /// is <c>awaiting-start</c>, the state that keeps the app from filing the morning's coffee as holiday spending.
    /// </summary>
    private static string State(FinApp.Domain.Budgeting.Trip t, DateOnly today) =>
        t.IsFinishedOn(today) ? "finished"
        : t.IsActiveOn(today) ? "active"
        : t.IsAwaitingStart(today) ? "awaiting-start"
        : "upcoming";
}
