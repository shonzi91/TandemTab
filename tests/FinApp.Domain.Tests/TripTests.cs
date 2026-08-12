using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class TripTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    private static (Account Account, Period Period, Guid Food, Guid Fund, Guid Member) Setup()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        return (account, period, food.Id, Guid.NewGuid(), Guid.NewGuid());
    }

    // --- The trip itself ------------------------------------------------------------------------------------

    [Fact]
    public void A_trip_cannot_end_before_it_starts()
    {
        var account = new Account("Personal", Eur);
        Assert.Throws<ArgumentException>(() =>
            account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 3)));
    }

    [Fact]
    public void A_one_day_trip_is_one_day_long_not_zero()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("Day out", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 10));

        Assert.Equal(1, trip.LengthInDays);
        // Active once confirmed — the dates open the window, the tap turns trip mode on (see StartedOn).
        account.StartTrip(trip.Id, new DateOnly(2026, 6, 10));
        Assert.True(trip.IsActiveOn(new DateOnly(2026, 6, 10)));
    }

    [Fact]
    public void Two_trips_cannot_share_a_name()
    {
        var account = new Account("Personal", Eur);
        account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        Assert.Throws<InvalidOperationException>(() =>
            account.AddTrip("rome", new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 5)));
    }

    // --- Trip mode is derived, never stored -----------------------------------------------------------------

    [Fact]
    public void Trip_mode_switches_itself_OFF_on_the_dates_even_though_it_is_switched_on_by_hand()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.StartTrip(trip.Id, new DateOnly(2026, 6, 10));   // the confirmation — a date is not a departure

        // The asymmetry is deliberate. Turning trip mode ON is a decision (the flight may be at six in the evening),
        // but turning it OFF is not: the trip's last day passes and it is over, so there is still no toggle anyone
        // can leave switched on by accident.
        Assert.Null(account.ActiveTrip(new DateOnly(2026, 6, 9)));      // the day before
        Assert.NotNull(account.ActiveTrip(new DateOnly(2026, 6, 10)));  // departure, inclusive
        Assert.NotNull(account.ActiveTrip(new DateOnly(2026, 6, 17)));  // return, inclusive
        Assert.Null(account.ActiveTrip(new DateOnly(2026, 6, 18)));     // home again
    }

    [Fact]
    public void Overlapping_trips_resolve_to_the_one_that_started_most_recently()
    {
        var account = new Account("Personal", Eur);
        var longHaul = account.AddTrip("Long haul", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var side = account.AddTrip("Side trip", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 12));
        account.StartTrip(longHaul.Id, new DateOnly(2026, 6, 1));
        account.StartTrip(side.Id, new DateOnly(2026, 6, 10));

        Assert.Equal("Side trip", account.ActiveTrip(new DateOnly(2026, 6, 11))!.Name);
    }

    // --- Membership is by link, not by date -----------------------------------------------------------------

    [Fact]
    public void A_booking_paid_months_early_counts_toward_the_trip_but_stays_in_its_own_period()
    {
        var account = new Account("Personal", Eur);
        var travel = account.AddCategory("Travel");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var march = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // A flight bought in March for a June trip.
        var flight = march.AddExpense(new Expense(travel.Id, M(220), new DateOnly(2026, 3, 4), member, fund));
        flight.SetTrip(trip.Id);

        // It belongs to the trip...
        Assert.Equal([flight.Id], account.TripExpenses(trip.Id).Select(e => e.Id));
        // ...and it is still March's spending, which is what keeps budgets and safe-to-spend honest.
        Assert.Contains(flight, march.Expenses);
        Assert.Equal(new DateOnly(2026, 3, 4), flight.Date);
    }

    [Fact]
    public void Editing_a_trip_expense_keeps_it_attached_to_the_trip()
    {
        var (account, period, food, fund, member) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var expense = period.AddExpense(new Expense(food, M(20), new DateOnly(2026, 6, 11), member, fund));
        expense.SetTrip(trip.Id);

        // An edit mints a new id (the ledger is append-only) — the trip link must ride along, or correcting a
        // typo silently drops the row out of the recap.
        var edited = period.EditExpense(expense.Id, food, M(25), fund, "corrected", new DateOnly(2026, 6, 11));

        Assert.NotEqual(expense.Id, edited.Id);
        Assert.Equal(trip.Id, edited.TripId);
    }

    [Fact]
    public void Moving_a_trips_dates_does_not_detach_its_expenses()
    {
        var (account, period, food, fund, member) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var expense = period.AddExpense(new Expense(food, M(40), new DateOnly(2026, 6, 11), member, fund));
        expense.SetTrip(trip.Id);

        account.UpdateTrip(trip.Id, "Rome", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 8));

        Assert.Single(account.TripExpenses(trip.Id));
    }

    [Fact]
    public void Removing_a_trip_detaches_its_expenses_rather_than_leaving_them_pointing_at_nothing()
    {
        var (account, period, food, fund, member) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var expense = period.AddExpense(new Expense(food, M(40), new DateOnly(2026, 6, 11), member, fund));
        expense.SetTrip(trip.Id);

        account.RemoveTrip(trip.Id);

        Assert.Null(expense.TripId);
        Assert.Contains(expense, period.Expenses);   // the money was still spent
    }

    // --- Currency: converted at entry, never re-applied ------------------------------------------------------

    [Fact]
    public void A_trip_rate_converts_into_the_account_currency()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("London", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 14));
        account.SetTripRate(trip.Id, "gbp", 1.17m);

        Assert.True(trip.HasRate);
        Assert.Equal("GBP", trip.SpendCurrency);
        Assert.Equal(23.40m, trip.ToAccountCurrency(20m));
    }

    [Fact]
    public void Clearing_either_half_of_the_rate_clears_both()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("London", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 14));
        account.SetTripRate(trip.Id, "GBP", 1.17m);

        account.SetTripRate(trip.Id, "GBP", null);

        Assert.False(trip.HasRate);
        Assert.Null(trip.SpendCurrency);
        Assert.Equal(20m, trip.ToAccountCurrency(20m));   // no rate → no conversion, not a zero
    }

    // --- The seeded trip tags -------------------------------------------------------------------------------

    [Fact]
    public void Trip_tags_are_seeded_once_and_a_second_call_adds_nothing()
    {
        var account = new Account("Personal", Eur);
        var seeds = new (string, string?, Guid?)[] { ("Stay", "🏨", null), ("Travel", "✈️", null) };

        account.EnsureTripTags(seeds);
        account.EnsureTripTags(seeds);

        Assert.Equal(2, account.TripTags.Count());
        Assert.Equal(2, account.Tags.Count);
    }

    [Fact]
    public void A_seed_adopts_a_tag_the_user_already_has_instead_of_colliding_with_it()
    {
        var account = new Account("Personal", Eur);
        var existing = account.AddTag("Food");

        account.EnsureTripTags([("food", "🍽️", null)]);

        Assert.Single(account.Tags);
        Assert.True(existing.IsTripTag);
        Assert.Equal("Food", existing.Name);   // the user's own capitalisation survives
    }

    [Fact]
    public void A_seeded_trip_tag_files_into_its_category_so_tagging_replaces_categorising()
    {
        var account = new Account("Personal", Eur);
        var stays = account.AddCategory("Housing");

        var tags = account.EnsureTripTags([("Stay", "🏨", stays.Id)]);

        Assert.Equal(stays.Id, tags[0].CategoryId);
    }

    // --- The recap ------------------------------------------------------------------------------------------

    [Fact]
    public void The_recap_splits_the_total_into_pre_paid_on_trip_and_after_return()
    {
        var account = new Account("Personal", Eur);
        var travel = account.AddCategory("Travel");
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var march = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var june = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        march.AddExpense(new Expense(travel.Id, M(220), new DateOnly(2026, 3, 4), member, fund)).SetTrip(trip.Id);
        june.AddExpense(new Expense(food.Id, M(60), new DateOnly(2026, 6, 12), member, fund)).SetTrip(trip.Id);
        june.AddExpense(new Expense(food.Id, M(15), new DateOnly(2026, 6, 20), member, fund)).SetTrip(trip.Id);
        june.AddExpense(new Expense(food.Id, M(99), new DateOnly(2026, 6, 12), member, fund));   // not on the trip

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.Equal(295m, recap.Spent.Amount);        // 220 + 60 + 15 — the untagged 99 stays out
        Assert.Equal(3, recap.ExpenseCount);
        Assert.Equal(220m, recap.PrePaid.Amount);
        Assert.Equal(60m, recap.OnTrip.Amount);
        Assert.Equal(15m, recap.AfterReturn.Amount);
    }

    [Fact]
    public void Per_day_uses_the_trips_length_not_the_span_of_its_expense_dates()
    {
        var account = new Account("Personal", Eur);
        var travel = account.AddCategory("Travel");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var march = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        // 8 days: 10th to 17th inclusive.
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        march.AddExpense(new Expense(travel.Id, M(800), new DateOnly(2026, 3, 4), member, fund)).SetTrip(trip.Id);

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.Equal(8, recap.LengthInDays);
        Assert.Equal(100m, recap.PerDay.Amount);   // not 800 spread over March→June
    }

    [Fact]
    public void The_recap_reports_what_came_out_of_the_linked_savings_bucket_without_discounting_it()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var holiday = account.AddSavingCategory("Holiday");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var june = account.StartPeriod(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.SetTripSavingCategory(trip.Id, holiday.Id);

        var fromSavings = new Expense(food.Id, M(500), new DateOnly(2026, 6, 12), member, fund,
            sourceSavingCategoryId: holiday.Id);
        june.AddExpense(fromSavings).SetTrip(trip.Id);
        june.AddExpense(new Expense(food.Id, M(120), new DateOnly(2026, 6, 13), member, fund)).SetTrip(trip.Id);

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.Equal(620m, recap.Spent.Amount);              // the money really left, all of it
        Assert.Equal(500m, recap.FundedFromSavings.Amount);  // and this much had been saved for it
    }

    [Fact]
    public void A_linked_bucket_that_isnt_in_the_account_is_rejected()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        Assert.Throws<InvalidOperationException>(() =>
            account.SetTripSavingCategory(trip.Id, Guid.NewGuid()));
    }

    [Fact]
    public void A_mostly_untagged_trip_says_its_tag_split_is_not_representative()
    {
        var (account, period, food, fund, member) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var stay = account.AddTag("Stay");

        period.AddExpense(new Expense(food, M(100), new DateOnly(2026, 6, 11), member, fund)).SetTrip(trip.Id);
        var tagged = period.AddExpense(new Expense(food, M(20), new DateOnly(2026, 6, 12), member, fund));
        tagged.SetTrip(trip.Id);
        tagged.SetTag(stay.Id);

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.Equal(100m, recap.Untagged.Amount);
        Assert.False(recap.TagsAreRepresentative);   // 20 of 120 tagged → lead with categories instead
        Assert.Single(recap.TagBreakdown);           // the tag slice is still there for anyone who wants it
    }

    [Fact]
    public void A_trip_with_nothing_attached_is_empty_rather_than_a_page_of_zeros()
    {
        var account = new Account("Personal", Eur);
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.True(recap.IsEmpty);
        Assert.Equal(0m, recap.Spent.Amount);
        Assert.Empty(recap.CategoryBreakdown);
    }

    [Fact]
    public void A_budget_reports_the_overspend()
    {
        var (account, period, food, fund, member) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.SetTripBudget(trip.Id, 500m);
        period.AddExpense(new Expense(food, M(620), new DateOnly(2026, 6, 11), member, fund)).SetTrip(trip.Id);

        var recap = new TripRecapService().Build(account, trip.Id)!;

        Assert.True(recap.IsOverBudget);
        Assert.Equal(120m, recap.AgainstBudget!.Value.Amount);
    }

    // --- Snapshot round-trip --------------------------------------------------------------------------------

    [Fact]
    public void Trips_and_trip_links_survive_a_snapshot_round_trip()
    {
        var (account, period, food, fund, member) = Setup();
        var holiday = account.AddSavingCategory("Holiday");
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17), "Rome, Italy", "🇮🇹");
        account.SetTripSavingCategory(trip.Id, holiday.Id);
        account.SetTripBudget(trip.Id, 900m);
        account.SetTripRate(trip.Id, "GBP", 1.17m);
        account.EnsureTripTags([("Stay", "🏨", food)]);
        period.AddExpense(new Expense(food, M(40), new DateOnly(2026, 6, 11), member, fund)).SetTrip(trip.Id);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        var rt = Assert.Single(restored.Trips);
        Assert.Equal("Rome", rt.Name);
        Assert.Equal("Rome, Italy", rt.Destination);
        Assert.Equal("🇮🇹", rt.Icon);
        Assert.Equal(new DateOnly(2026, 6, 10), rt.From);
        Assert.Equal(new DateOnly(2026, 6, 17), rt.To);
        Assert.Equal(holiday.Id, rt.SavingCategoryId);
        Assert.Equal(900m, rt.Budget);
        Assert.Equal("GBP", rt.SpendCurrency);
        Assert.Equal(1.17m, rt.Rate);
        Assert.Single(restored.TripTags);
        Assert.Single(restored.TripExpenses(trip.Id));
    }

    [Fact]
    public void A_snapshot_written_before_trips_existed_loads_with_none()
    {
        var (account, period, food, fund, member) = Setup();
        period.AddExpense(new Expense(food, M(40), new DateOnly(2026, 6, 11), member, fund));

        // Serialize, then strip the property a pre-trips writer would never have emitted.
        var payload = AccountSnapshotSerializer.Serialize(account).Replace("\"Trips\":null,", "").Replace(",\"Trips\":null", "");
        var restored = AccountSnapshotSerializer.Deserialize(payload);

        Assert.Empty(restored.Trips);
        Assert.Null(restored.ActiveTrip(new DateOnly(2026, 6, 11)));
        Assert.Single(restored.Periods.SelectMany(p => p.Expenses));
    }

    // --- Filing the whole trip into one category ------------------------------------------------------------

    [Fact]
    public void A_trip_files_per_label_until_it_is_given_a_category()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // The default is the original behaviour, so every trip that already exists keeps filing as it did.
        Assert.Null(trip.CategoryId);
        Assert.False(trip.FilesIntoOneCategory);
    }

    [Fact]
    public void A_trip_can_collect_into_one_category_and_let_it_go_again()
    {
        var (account, _, food, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        account.SetTripCategory(trip.Id, food);
        Assert.Equal(food, trip.CategoryId);
        Assert.True(trip.FilesIntoOneCategory);

        // Clearing goes back to per-label filing rather than leaving a dangling id.
        account.SetTripCategory(trip.Id, null);
        Assert.Null(trip.CategoryId);
        Assert.False(trip.FilesIntoOneCategory);
    }

    [Fact]
    public void A_trip_category_must_exist_in_the_account()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // A link to nothing would silently fall back to per-label filing, which reads as the setting being ignored.
        Assert.Throws<InvalidOperationException>(() => account.SetTripCategory(trip.Id, Guid.NewGuid()));
        Assert.Null(trip.CategoryId);
    }

    [Fact]
    public void The_trip_category_survives_a_snapshot_round_trip()
    {
        var (account, _, food, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.SetTripCategory(trip.Id, food);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Equal(food, restored.FindTrip(trip.Id)!.CategoryId);
    }

    [Fact]
    public void A_trip_snapshot_written_before_the_category_existed_files_per_label()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // Body data as a trailing optional: a writer that never knew about CategoryId omits it, and the property
        // has to land on null — i.e. the behaviour that trip already had — rather than throwing or defaulting to
        // some category. This is the guarantee that makes the change need no migration.
        var payload = AccountSnapshotSerializer.Serialize(account)
            .Replace(",\"CategoryId\":null", "").Replace("\"CategoryId\":null,", "");
        var restored = AccountSnapshotSerializer.Deserialize(payload);

        var rt = restored.FindTrip(trip.Id)!;
        Assert.Null(rt.CategoryId);
        Assert.False(rt.FilesIntoOneCategory);
        Assert.Equal("Rome", rt.Name);
    }

    // --- Finishing a trip: "finish" has to mean OVER --------------------------------------------------------

    [Fact]
    public void Finishing_a_trip_ends_it_today_and_it_stops_being_active_the_same_day()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var backEarly = new DateOnly(2026, 6, 13);

        account.FinishTrip(trip.Id, backEarly);

        // The end date tells the truth about when it ended...
        Assert.Equal(backEarly, trip.To);
        Assert.Equal(backEarly, trip.FinishedOn);
        // ...and the app stops wearing it THAT day, not at midnight. This is the whole point: pulling the end date
        // back to today would still leave today inside [From..To], so the Home hero and the form's default stayed.
        Assert.False(trip.IsActiveOn(backEarly));
        Assert.True(trip.IsFinishedOn(backEarly));
        Assert.Null(account.ActiveTrip(backEarly));
    }

    [Fact]
    public void Finishing_a_trip_that_has_not_left_yet_leaves_its_dates_alone()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // Cancelled before departure. The dates cannot be pulled back (a trip can't end before it starts) and there
        // is nothing to correct anyway — what changes is that it is no longer coming.
        account.FinishTrip(trip.Id, new DateOnly(2026, 6, 1));

        Assert.Equal(new DateOnly(2026, 6, 10), trip.From);
        Assert.Equal(new DateOnly(2026, 6, 17), trip.To);
        Assert.False(trip.IsUpcomingOn(new DateOnly(2026, 6, 1)));
        Assert.Empty(account.UpcomingTrips(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void Reopening_a_finished_trip_puts_it_back_on_the_road()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.StartTrip(trip.Id, new DateOnly(2026, 6, 10));
        account.FinishTrip(trip.Id, new DateOnly(2026, 6, 13));

        account.ReopenTrip(trip.Id);

        Assert.Null(trip.FinishedOn);
        // The pulled-in end date is deliberately NOT restored — we no longer know what it was.
        Assert.Equal(new DateOnly(2026, 6, 13), trip.To);
        // Still started, so reopening puts it straight back on the road rather than asking "have you left?" again.
        Assert.True(trip.IsActiveOn(new DateOnly(2026, 6, 13)));
    }

    [Fact]
    public void A_finished_trip_survives_a_snapshot_round_trip_without_being_re_finished()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.FinishTrip(trip.Id, new DateOnly(2026, 6, 13));

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        // Restored, never replayed: calling Finish(today) on load would re-cut the end date against whatever day
        // the snapshot happens to be read on.
        var rt = restored.FindTrip(trip.Id)!;
        Assert.Equal(new DateOnly(2026, 6, 13), rt.FinishedOn);
        Assert.Equal(new DateOnly(2026, 6, 13), rt.To);
    }

    [Fact]
    public void A_trip_snapshot_written_before_finishing_existed_is_simply_not_finished()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        var payload = AccountSnapshotSerializer.Serialize(account)
            .Replace(",\"FinishedOn\":null", "").Replace(",\"SavingsApplied\":0", "");
        var restored = AccountSnapshotSerializer.Deserialize(payload);

        var rt = restored.FindTrip(trip.Id)!;
        Assert.Null(rt.FinishedOn);
        Assert.Equal(0m, rt.SavingsApplied);
        // Not finished — and, since this snapshot predates opt-in start too, awaiting its confirmation rather than
        // running. See the round-trip test in the opt-in section for why that default is the safe one.
        Assert.False(rt.IsFinishedOn(new DateOnly(2026, 6, 12)));
        Assert.True(rt.IsAwaitingStart(new DateOnly(2026, 6, 12)));
    }

    // --- Opt-in start: a date is not a departure ------------------------------------------------------------

    [Fact]
    public void A_trips_dates_arriving_does_not_switch_trip_mode_on()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var departureDay = new DateOnly(2026, 6, 10);

        // The window is open, but nobody has said they've left — the flight may be at six in the evening.
        Assert.True(trip.IsAwaitingStart(departureDay));
        Assert.False(trip.IsActiveOn(departureDay));
        Assert.Null(account.ActiveTrip(departureDay));
        Assert.Single(account.TripsAwaitingStart(departureDay));

        account.StartTrip(trip.Id, departureDay);

        Assert.False(trip.IsAwaitingStart(departureDay));
        Assert.True(trip.IsActiveOn(departureDay));
        Assert.Equal(trip.Id, account.ActiveTrip(departureDay)!.Id);
    }

    [Fact]
    public void A_trip_cannot_be_started_outside_its_own_window()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        Assert.Throws<InvalidOperationException>(() => account.StartTrip(trip.Id, new DateOnly(2026, 6, 9)));
        Assert.Throws<InvalidOperationException>(() => account.StartTrip(trip.Id, new DateOnly(2026, 6, 18)));
        Assert.Null(trip.StartedOn);
    }

    [Fact]
    public void Confirming_a_departure_late_does_not_move_the_trips_dates()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // Confirmed two days in. The PLAN is what the recap's booked-ahead split is measured against, so a late
        // confirmation must not silently re-file the bookings made before the 10th.
        account.StartTrip(trip.Id, new DateOnly(2026, 6, 12));

        Assert.Equal(new DateOnly(2026, 6, 10), trip.From);
        Assert.Equal(new DateOnly(2026, 6, 12), trip.StartedOn);
        Assert.Equal(3, trip.DayOn(new DateOnly(2026, 6, 12)));
    }

    [Fact]
    public void An_unstarted_trip_is_not_upcoming_once_its_dates_have_arrived()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var day = new DateOnly(2026, 6, 12);

        // It is in none of the three ordinary buckets — it is a question waiting for an answer, and the UI has a
        // fourth state for exactly that.
        Assert.False(trip.IsUpcomingOn(day));
        Assert.False(trip.IsActiveOn(day));
        Assert.False(trip.IsFinishedOn(day));
        Assert.True(trip.IsAwaitingStart(day));
    }

    [Fact]
    public void A_started_trip_survives_a_snapshot_round_trip_and_an_old_one_asks_first()
    {
        var (account, _, _, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        account.StartTrip(trip.Id, new DateOnly(2026, 6, 10));

        var payload = AccountSnapshotSerializer.Serialize(account);
        Assert.Equal(new DateOnly(2026, 6, 10),
            AccountSnapshotSerializer.Deserialize(payload).FindTrip(trip.Id)!.StartedOn);

        // A snapshot written before opt-in start existed has no confirmation — so a trip that was running reads as
        // awaiting start and costs one tap. Deliberate: defaulting it to "started" would re-introduce the automatic
        // trip mode this replaced.
        var legacy = payload.Replace(",\"StartedOn\":\"2026-06-10\"", "");
        var rt = AccountSnapshotSerializer.Deserialize(legacy).FindTrip(trip.Id)!;
        Assert.Null(rt.StartedOn);
        Assert.True(rt.IsAwaitingStart(new DateOnly(2026, 6, 12)));
    }

    // --- ⛔ The rate belongs to the money, not the journey ---------------------------------------------------

    [Fact]
    public void A_fund_holding_account_currency_money_never_converts()
    {
        var account = new Account("Personal", Eur);
        var bank = account.AddFund("Bank");

        // The card case, and the whole reason this moved off the trip: the bank already converted, the statement is
        // in the account currency, and typing 50 must store 50 — even while a rated trip is running.
        Assert.False(bank.HasRate);
        Assert.Equal(50m, bank.ToAccountCurrency(50m));
    }

    [Fact]
    public void A_wallet_of_foreign_cash_converts_at_the_rate_it_was_bought_at()
    {
        var account = new Account("Personal", Eur);
        var wallet = account.AddFund("Lisbon cash");

        account.SetFundCurrency(wallet.Id, "gbp", 1.17m);

        Assert.True(wallet.HasRate);
        Assert.Equal("GBP", wallet.Currency);           // normalised, like every other currency code in the app
        Assert.Equal(234m, wallet.ToAccountCurrency(200m));
        // Rounded to the cent, away from zero — the same discipline the trip rate used.
        Assert.Equal(11.70m, wallet.ToAccountCurrency(10m));
    }

    [Fact]
    public void A_wallet_can_name_its_currency_before_anyone_knows_the_rate()
    {
        var account = new Account("Personal", Eur);
        var wallet = account.AddFund("Lisbon cash");

        // ★ The state the creation form produces: "this wallet is for pounds, I'll buy them tomorrow." The two
        // facts arrive at different moments, so they must be storable at different moments — these used to clear
        // together and the wallet silently saved as an ordinary one.
        account.SetFundCurrency(wallet.Id, "gbp", null);
        Assert.Equal("GBP", wallet.Currency);
        Assert.Null(wallet.Rate);
        // No rate means nothing converts, which is exactly right until the loading transfer derives one.
        Assert.False(wallet.HasRate);
        Assert.Equal(200m, wallet.ToAccountCurrency(200m));

        account.SetFundCurrency(wallet.Id, "GBP", 1.17m);
        Assert.True(wallet.HasRate);
        Assert.Equal(234m, wallet.ToAccountCurrency(200m));
    }

    [Fact]
    public void Clearing_a_wallets_currency_drops_the_rate_with_it()
    {
        var account = new Account("Personal", Eur);
        var wallet = account.AddFund("Lisbon cash");
        account.SetFundCurrency(wallet.Id, "GBP", 1.17m);

        // A rate with no currency has nothing to label the field with, so the currency is what owns the pair.
        account.SetFundCurrency(wallet.Id, null, 1.17m);

        Assert.Null(wallet.Currency);
        Assert.Null(wallet.Rate);
        // Clearing converts nothing retroactively: amounts were stored converted at entry time.
        Assert.Equal(200m, wallet.ToAccountCurrency(200m));
    }

    [Fact]
    public void A_funds_currency_survives_a_snapshot_round_trip_and_an_old_fund_has_none()
    {
        var account = new Account("Personal", Eur);
        var wallet = account.AddFund("Lisbon cash");
        account.SetFundCurrency(wallet.Id, "GBP", 1.17m);

        var payload = AccountSnapshotSerializer.Serialize(account);
        var restored = AccountSnapshotSerializer.Deserialize(payload).FindFund(wallet.Id)!;
        Assert.Equal("GBP", restored.Currency);
        Assert.Equal(1.17m, restored.Rate);

        // A snapshot written before the rate moved off the trip carries neither field — which reads as an ordinary
        // account-currency wallet, i.e. exactly what those funds were.
        var legacy = payload.Replace(",\"Currency\":\"GBP\",\"Rate\":1.17", "");
        var old = AccountSnapshotSerializer.Deserialize(legacy).FindFund(wallet.Id)!;
        Assert.Null(old.Currency);
        Assert.False(old.HasRate);
    }

    // --- Releasing saved money into a trip's budget ---------------------------------------------------------

    [Fact]
    public void Releasing_saved_money_needs_both_a_bucket_and_a_category()
    {
        var (account, _, food, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));

        // No bucket: nothing to release.
        Assert.Throws<InvalidOperationException>(() => account.ApplyTripSavings(trip.Id, 100m));

        var bucket = account.AddSavingCategory("Holiday");
        account.SetTripSavingCategory(trip.Id, bucket.Id);
        // Bucket but no category: nowhere to release it INTO.
        Assert.Throws<InvalidOperationException>(() => account.ApplyTripSavings(trip.Id, 100m));

        account.SetTripCategory(trip.Id, food);
        account.ApplyTripSavings(trip.Id, 100m);
        Assert.Equal(100m, trip.SavingsApplied);
    }

    [Fact]
    public void Released_savings_accumulate_and_survive_a_round_trip()
    {
        var (account, _, food, _, _) = Setup();
        var trip = account.AddTrip("Rome", new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 17));
        var bucket = account.AddSavingCategory("Holiday");
        account.SetTripSavingCategory(trip.Id, bucket.Id);
        account.SetTripCategory(trip.Id, food);

        // Additive: "release some now, some more later" is the real flow, and a setter would lose the first release.
        account.ApplyTripSavings(trip.Id, 400m);
        account.ApplyTripSavings(trip.Id, 350m);
        Assert.Equal(750m, trip.SavingsApplied);
        Assert.Throws<ArgumentException>(() => account.ApplyTripSavings(trip.Id, 0m));

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(750m, restored.FindTrip(trip.Id)!.SavingsApplied);
    }
}
