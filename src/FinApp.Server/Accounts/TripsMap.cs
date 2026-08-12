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
