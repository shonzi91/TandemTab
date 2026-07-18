using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>The cash-flow runway: given what money does each month, when does the balance run out? Projection only —
/// it moves nothing, and deliberately ignores budgets (advisory plans, not commitments).</summary>
public class CashFlowForecastTests
{
    private static readonly DateOnly From = new(2026, 7, 15);   // mid-month: rows must still be dated to the 1st

    [Fact]
    public void A_surplus_compounds_forward_month_on_month()
    {
        var p = CashFlowForecast.Project(1_000m, income: 2_000m, spending: 1_500m, From, 3);

        Assert.Equal(3, p.Months.Count);
        Assert.Null(p.FirstShortfallMonth);
        Assert.Equal(new DateOnly(2026, 7, 1), p.Months[0].Month);   // dated to the 1st, not the 15th
        Assert.Equal(1_500m, p.Months[0].Closing);
        Assert.Equal(2_000m, p.Months[1].Closing);
        Assert.Equal(2_500m, p.Months[2].Closing);
        Assert.Equal(p.Months[0].Closing, p.Months[1].Opening);      // each month opens where the last closed
        Assert.Equal(p.Months[1].Closing, p.Months[2].Opening);
    }

    [Fact]
    public void It_names_the_first_month_the_balance_goes_negative()
    {
        var p = CashFlowForecast.Project(500m, income: 1_000m, spending: 1_200m, From, 6);

        Assert.Equal(new DateOnly(2026, 9, 1), p.FirstShortfallMonth);
        Assert.Equal(300m, p.Months[0].Closing);
        Assert.Equal(100m, p.Months[1].Closing);
        Assert.Equal(-100m, p.Months[2].Closing);
        Assert.Equal(-300m, p.Months[3].Closing);   // keeps going rather than clamping at zero
    }

    [Fact]
    public void Earmarked_savings_are_reported_but_never_subtracted_from_the_balance()
    {
        // The bug this pins: money set aside has NOT left the account (Period.ExpectedClosingBalance doesn't
        // subtract it either), so taking it off a balance projection double-counts against a balance that already
        // contains it — and turned a healthy account into a false "runs short" warning.
        var plain = CashFlowForecast.Project(1_000m, 2_000m, 1_800m, From, 6);
        var committed = CashFlowForecast.Project(1_000m, 2_000m, 1_800m, From, 6, monthlyCommitted: 600m);

        Assert.Equal(plain.Months[0].Closing, committed.Months[0].Closing);
        Assert.Equal(1_200m, committed.Months[0].Closing);
        Assert.Null(committed.FirstShortfallMonth);       // still solvent — the earmark didn't spend anything
        Assert.Equal(600m, committed.MonthlyCommitted);   // but it is carried through for display
    }

    [Fact]
    public void An_account_with_no_movement_projects_a_flat_line_rather_than_failing()
    {
        var p = CashFlowForecast.Project(750m, 0m, 0m, From, 3);

        Assert.Equal(3, p.Months.Count);
        Assert.All(p.Months, m => Assert.Equal(750m, m.Closing));
        Assert.Null(p.FirstShortfallMonth);
    }

    [Fact]
    public void A_balance_that_starts_negative_reports_the_very_first_month()
    {
        var p = CashFlowForecast.Project(-50m, 1_000m, 1_000m, From, 2);
        Assert.Equal(new DateOnly(2026, 7, 1), p.FirstShortfallMonth);
    }

    [Theory]
    [InlineData(0, 1)]      // clamped up — a zero-month projection is meaningless
    [InlineData(-5, 1)]
    [InlineData(999, CashFlowForecast.MaxMonths)]
    public void The_horizon_is_clamped_to_something_defensible(int asked, int expected)
    {
        var p = CashFlowForecast.Project(100m, 0m, 0m, From, asked);
        Assert.Equal(expected, p.Months.Count);
    }

    [Fact]
    public void Negative_inputs_cannot_conjure_money()
    {
        var p = CashFlowForecast.Project(100m, income: -500m, spending: -500m, From, 1, monthlyCommitted: -50m);
        Assert.Equal(0m, p.Months[0].Income);
        Assert.Equal(0m, p.Months[0].Spending);
        Assert.Equal(0m, p.MonthlyCommitted);
        Assert.Equal(100m, p.Months[0].Closing);
    }

    [Fact]
    public void The_basis_travels_with_the_projection_so_the_user_knows_what_it_rests_on()
    {
        var history = CashFlowForecast.Project(100m, 10m, 5m, From, 1, CashFlowBasis.Demonstrated);
        var declared = CashFlowForecast.Project(100m, 10m, 5m, From, 1, CashFlowBasis.Recurring, hasUnknownAmounts: true);

        Assert.Equal(CashFlowBasis.Demonstrated, history.Basis);
        Assert.False(history.HasUnknownAmounts);
        Assert.Equal(CashFlowBasis.Recurring, declared.Basis);
        Assert.True(declared.HasUnknownAmounts);
    }

    [Fact]
    public void Projection_rows_run_in_order_across_a_year_boundary()
    {
        var p = CashFlowForecast.Project(0m, 0m, 0m, new DateOnly(2026, 11, 10), 4);

        Assert.Equal(new DateOnly(2026, 11, 1), p.Months[0].Month);
        Assert.Equal(new DateOnly(2026, 12, 1), p.Months[1].Month);
        Assert.Equal(new DateOnly(2027, 1, 1), p.Months[2].Month);
        Assert.Equal(new DateOnly(2027, 2, 1), p.Months[3].Month);
    }

    // ── The demonstrated basis: averaging what actually happened ──────────────────────────────
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    /// <summary>A period with <paramref name="income"/> paid in and <paramref name="spent"/> paid out.</summary>
    private static Period Month(int month, decimal income, decimal spent, bool closed = true)
    {
        var p = new Period(Eur, new DateOnly(2026, month, 1), new DateOnly(2026, month, 28));
        var member = Guid.NewGuid();
        if (income > 0m) p.Deposit(member, M(income));
        if (spent > 0m) p.AddExpense(new Expense(Guid.NewGuid(), M(spent), new DateOnly(2026, month, 10), member, Guid.NewGuid()));
        if (closed) p.Close();
        return p;
    }

    [Fact]
    public void Demonstrated_averages_what_actually_came_in_and_went_out()
    {
        var seen = CashFlowForecast.Demonstrated(new[] { Month(1, 3_000m, 2_000m), Month(2, 3_500m, 2_400m) });

        Assert.NotNull(seen);
        Assert.Equal(3_250m, seen!.Value.Income);     // (3000 + 3500) / 2
        Assert.Equal(2_200m, seen.Value.Spending);    // (2000 + 2400) / 2
    }

    [Fact]
    public void The_period_still_in_progress_is_excluded_from_the_average()
    {
        // Checking on the 3rd of the month, the open period has barely any income in it yet. Averaging it in
        // would drag the projection down purely because of when you looked.
        var seen = CashFlowForecast.Demonstrated(new[]
        {
            Month(1, 3_000m, 2_000m),
            Month(2, 3_000m, 2_000m),
            Month(3, 100m, 50m, closed: false),
        });

        Assert.Equal(3_000m, seen!.Value.Income);
        Assert.Equal(2_000m, seen.Value.Spending);
    }

    [Fact]
    public void No_completed_period_means_no_demonstrated_basis_at_all()
    {
        Assert.Null(CashFlowForecast.Demonstrated(new[] { Month(1, 3_000m, 2_000m, closed: false) }));
        Assert.Null(CashFlowForecast.Demonstrated(Array.Empty<Period>()));
    }

    [Fact]
    public void A_real_account_that_earns_more_than_it_spends_is_never_reported_as_running_short()
    {
        // The reported case: ~€4,877 in and ~€2,629 out per period on a €1,033 balance. The first version read
        // income only from declared recurring items, saw €0 in, and warned of running short immediately.
        var p = CashFlowForecast.Project(1_033.33m, 4_876.86m, 2_629.35m, From, 6, monthlyCommitted: 250m);

        Assert.Null(p.FirstShortfallMonth);
        Assert.True(p.Months[5].Closing > p.Months[0].Closing);   // it grows, as it should
    }
}
