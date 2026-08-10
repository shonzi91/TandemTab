using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// Reading an amount as the time behind it. The rate is a number the user types — never derived from income ÷
/// working days, which is wrong for anyone freelance, part-time or on shifts — so "not set" has to be a real,
/// durable state rather than something the app fills in.
/// </summary>
public class HourlyRateTests
{
    private const string Eur = "EUR";

    [Fact]
    public void A_new_account_has_no_rate_and_therefore_no_time_cost()
    {
        var account = new Account("Personal", Eur);
        Assert.Null(account.HourlyRate);
        Assert.Null(account.TimeCostOf(50m));   // no rate → no claim, not a zero
    }

    [Fact]
    public void Setting_a_rate_turns_amounts_into_time()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(20m);

        Assert.Equal(TimeSpan.FromHours(1), account.TimeCostOf(20m));
        Assert.Equal(TimeSpan.FromMinutes(30), account.TimeCostOf(10m));
        Assert.Equal(TimeSpan.FromHours(2.5), account.TimeCostOf(50m));
    }

    [Fact]
    public void The_rate_is_rounded_to_the_minute_because_it_is_an_estimate()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(17.50m);
        // 3.50 / 17.50 = 0.2h = 12m exactly; 3.33 lands between minutes and must not carry seconds.
        Assert.Equal(TimeSpan.FromMinutes(12), account.TimeCostOf(3.50m));
        Assert.Equal(0, account.TimeCostOf(3.33m)!.Value.Seconds);
    }

    [Fact]
    public void Zero_clears_the_rate_rather_than_storing_a_useless_one()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(30m);
        account.SetHourlyRate(0m);

        Assert.Null(account.HourlyRate);
        Assert.Null(account.TimeCostOf(100m));
    }

    [Fact]
    public void Null_clears_the_rate()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(30m);
        account.SetHourlyRate(null);

        Assert.Null(account.HourlyRate);
    }

    [Fact]
    public void A_negative_rate_is_refused()
    {
        var account = new Account("Personal", Eur);
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetHourlyRate(-5m));
        Assert.Null(account.HourlyRate);
    }

    [Fact]
    public void A_zero_or_negative_amount_has_no_time_cost()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(25m);
        Assert.Null(account.TimeCostOf(0m));
        Assert.Null(account.TimeCostOf(-10m));
    }

    // --- Deriving the rate from income + a working pattern ---------------------------------------------------

    private static Account WithIncome(decimal income)
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var member = account.AddMember(Guid.NewGuid(), "Me").Id;
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        if (income > 0m) period.Deposit(member, new Money(income, Eur));
        return account;
    }

    [Fact]
    public void A_working_pattern_derives_the_rate_from_this_periods_income()
    {
        var account = WithIncome(3200m);
        account.SetWorkingPattern(20, 8m);   // 160 hours

        Assert.Equal(20m, account.EffectiveHourlyRate);
        Assert.Equal(TimeSpan.FromHours(1), account.TimeCostOf(20m));
    }

    /// <summary>The consequence of deriving it: the rate moves with income, so a lean month prices hours cheaper
    /// and every purchase reads as longer. Pinned deliberately — it's the trade-off, not an accident.</summary>
    [Fact]
    public void The_derived_rate_follows_income_so_a_lean_month_makes_things_look_more_expensive()
    {
        var rich = WithIncome(3200m);
        rich.SetWorkingPattern(20, 8m);
        var lean = WithIncome(1600m);
        lean.SetWorkingPattern(20, 8m);

        Assert.Equal(TimeSpan.FromHours(2), rich.TimeCostOf(40m));
        Assert.Equal(TimeSpan.FromHours(4), lean.TimeCostOf(40m));   // same 40, twice the hours
    }

    [Fact]
    public void A_typed_rate_outranks_the_working_pattern()
    {
        var account = WithIncome(3200m);
        account.SetWorkingPattern(20, 8m);   // would derive 20/h
        account.SetHourlyRate(50m);

        Assert.Equal(50m, account.EffectiveHourlyRate);
    }

    /// <summary>
    /// The two figures answer different questions — what an hour is WORTH vs what it actually PAID — so both stay
    /// computable side by side, and the gap between them is the useful number.
    /// </summary>
    [Fact]
    public void The_derived_rate_stays_visible_alongside_a_typed_one_so_the_gap_can_be_reported()
    {
        var account = WithIncome(5000m);
        account.SetWorkingPattern(20, 8m);   // 160h → 31.25/h actually earned
        account.SetHourlyRate(20m);          // but the user says their hour is worth 20

        Assert.Equal(20m, account.EffectiveHourlyRate);       // the typed one still prices time
        Assert.Equal(31.25m, account.DerivedHourlyRate);      // and the reality is still knowable
        Assert.Equal(0.64m, account.HourlyRateDrift);         // typed is 64% of what the hours paid
    }

    [Fact]
    public void On_a_lean_month_the_drift_runs_the_other_way()
    {
        var account = WithIncome(1000m);
        account.SetWorkingPattern(20, 8m);   // 160h → 6.25/h
        account.SetHourlyRate(20m);

        Assert.Equal(6.25m, account.DerivedHourlyRate);
        Assert.Equal(3.2m, account.HourlyRateDrift);   // typed is 3.2× what the hours paid
    }

    [Fact]
    public void There_is_no_drift_to_report_without_both_numbers()
    {
        var typedOnly = new Account("Personal", Eur);
        typedOnly.SetHourlyRate(20m);
        Assert.Null(typedOnly.HourlyRateDrift);

        var derivedOnly = WithIncome(3200m);
        derivedOnly.SetWorkingPattern(20, 8m);
        Assert.Null(derivedOnly.HourlyRateDrift);
    }

    [Fact]
    public void With_no_income_yet_nothing_is_claimed_rather_than_dividing_by_hope()
    {
        var account = WithIncome(0m);
        account.SetWorkingPattern(20, 8m);

        Assert.Null(account.EffectiveHourlyRate);
        Assert.Null(account.TimeCostOf(40m));
    }

    [Fact]
    public void A_half_filled_pattern_derives_nothing()
    {
        var account = WithIncome(3200m);
        account.SetWorkingPattern(20, null);
        Assert.Null(account.EffectiveHourlyRate);

        account.SetWorkingPattern(null, 8m);
        Assert.Null(account.EffectiveHourlyRate);
    }

    [Theory]
    [InlineData(32, 8)]
    [InlineData(20, 25)]
    public void An_impossible_working_pattern_is_refused(int days, int hours)
    {
        var account = new Account("Personal", Eur);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => account.SetWorkingPattern(days, hours));
    }

    /// <summary>The rate rides in the snapshot; an account saved before the feature existed must load with it unset.</summary>
    [Fact]
    public void The_rate_survives_a_snapshot_round_trip_and_a_legacy_snapshot_loads_without_one()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(42.50m);
        account.SetWorkingPattern(18, 7.5m);
        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(42.50m, restored.HourlyRate);
        Assert.Equal(18, restored.WorkingDaysPerMonth);
        Assert.Equal(7.5m, restored.WorkingHoursPerDay);

        // A payload written before the field existed simply has no such property.
        var legacy = AccountSnapshotSerializer.Serialize(new Account("Old", Eur))
            .Replace(",\"HourlyRate\":null", "");
        Assert.DoesNotContain("HourlyRate", legacy);
        Assert.Null(AccountSnapshotSerializer.Deserialize(legacy).HourlyRate);
    }
}
