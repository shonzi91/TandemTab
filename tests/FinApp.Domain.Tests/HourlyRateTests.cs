using FinApp.Domain.Accounts;
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

    /// <summary>The rate rides in the snapshot; an account saved before the feature existed must load with it unset.</summary>
    [Fact]
    public void The_rate_survives_a_snapshot_round_trip_and_a_legacy_snapshot_loads_without_one()
    {
        var account = new Account("Personal", Eur);
        account.SetHourlyRate(42.50m);
        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(42.50m, restored.HourlyRate);

        // A payload written before the field existed simply has no such property.
        var legacy = AccountSnapshotSerializer.Serialize(new Account("Old", Eur))
            .Replace(",\"HourlyRate\":null", "");
        Assert.DoesNotContain("HourlyRate", legacy);
        Assert.Null(AccountSnapshotSerializer.Deserialize(legacy).HourlyRate);
    }
}
