using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class InsightsServiceTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void An_empty_period_has_no_data_to_score()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.False(new InsightsService().Build(account, 0).HasData);
    }

    [Fact]
    public void An_out_of_range_period_index_returns_an_empty_report()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();

        Assert.False(new InsightsService().Build(account, 0).HasData);   // no periods at all
    }

    [Fact]
    public void Income_and_spending_produce_a_scored_report_with_a_category_breakdown()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(2000));
        p.AddExpense(new Expense(food, M(500), new DateOnly(2026, 1, 10), Guid.NewGuid(), fund));

        var report = new InsightsService().Build(account, 0);

        Assert.True(report.HasData);
        Assert.InRange(report.Score, 0, 100);
        var foodRow = Assert.Single(report.Breakdown, c => c.Name == "Food");
        // The breakdown carries the category's RAW stored icon — null here (no explicit icon), NOT a guessed display
        // icon. Resolving to a display icon is now the client's job (CategoryIcons decoupled from the domain service).
        Assert.Null(foodRow.Icon);
    }

    [Fact]
    public void The_narrative_is_language_independent_coded_messages_not_baked_english()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(2000));
        p.AddExpense(new Expense(food, M(500), new DateOnly(2026, 1, 10), Guid.NewGuid(), fund));

        var report = new InsightsService().Build(account, 0);

        // The verdict is a code from the band catalogue, not a formatted sentence.
        Assert.Contains(report.Verdict.Code, new[] { InsightCodes.VerdictHealthy, InsightCodes.VerdictAverage, InsightCodes.VerdictAtRisk });
        // The summary is one fragment (no prior period → no score-movement clause).
        var summary = Assert.Single(report.Summary);
        Assert.Contains(summary.Code, new[] { InsightCodes.SummaryHealthy, InsightCodes.SummaryAverage, InsightCodes.SummaryAtRisk });
    }

    [Fact]
    public void A_savings_shortfall_produces_a_critique_with_a_percent_target_arg()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(2000));
        // Spend everything → nothing saved → below the 20% target → "none yet" critique + shortfall tail.
        p.AddExpense(new Expense(food, M(2000), new DateOnly(2026, 1, 10), Guid.NewGuid(), fund));

        var report = new InsightsService().Build(account, 0);

        Assert.NotNull(report.SavingsShortfall);
        // Base critique + shortfall tail = two coded fragments; the base carries a Percent arg (the target).
        Assert.Equal(2, report.SavingsCritique.Count);
        Assert.Equal(InsightCodes.CritNoneYet, report.SavingsCritique[0].Code);
        Assert.Equal(InsightArgKind.Percent, report.SavingsCritique[0].Args[0].Kind);
        Assert.Equal(InsightsService.DefaultSavingsTarget, report.SavingsCritique[0].Args[0].Number);
        Assert.Equal(InsightCodes.CritTailShort, report.SavingsCritique[1].Code);
        // A "no savings set aside" warning signal is present, carrying the dash badge.
        Assert.Contains(report.Signals, s => s.Kind == SignalKind.Warn && s.Title.Code == InsightCodes.SigNoSavingsTitle);
    }

    /// <summary>Builds a two-period account where "Food" spikes from 100 → 1000 in the second period (a clear "running
    /// high" spike). <paramref name="foodBudget"/>>0 puts a budget on the spiking period.</summary>
    private static FinancialHealthReport SpikeReport(decimal foodBudget)
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.Deposit(me.UserId, M(3000));
        jan.AddExpense(new Expense(food, M(100), new DateOnly(2026, 1, 10), me.UserId, fund));   // baseline

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.Deposit(me.UserId, M(3000));
        feb.AddExpense(new Expense(food, M(1000), new DateOnly(2026, 2, 10), me.UserId, fund));  // 10× the baseline
        if (foodBudget > 0m) feb.SetBudget(food, M(foodBudget));

        return new InsightsService().Build(account, 1);   // score the February period
    }

    [Fact]
    public void A_spiking_category_with_no_budget_signals_running_high()
    {
        var report = SpikeReport(foodBudget: 0m);
        Assert.Contains(report.Signals, s => s.Kind == SignalKind.Warn && s.Title.Code == InsightCodes.SigCatHighTitle);
    }

    [Fact]
    public void A_spiking_category_that_is_over_budget_does_not_also_signal_running_high()
    {
        // Food spends 1000 against a 200 budget — over budget, which is surfaced by its own ring/alert. The spike
        // signal must not double-flag it ("over budget ⇒ running high"), so no SigCatHigh here.
        var report = SpikeReport(foodBudget: 200m);
        Assert.DoesNotContain(report.Signals, s => s.Title.Code == InsightCodes.SigCatHighTitle);
    }

    [Fact]
    public void Spending_a_sinking_fund_does_not_make_the_app_say_nothing_was_set_aside()
    {
        // Owner-reported: "no savings set aside" while the period plainly had savings. The signal asked
        // SavingsNetTotal (allocations MINUS drawdowns) while the Saved card asks SavingsSetAsideTotal, so a
        // period that saved AND deployed an older earmark nets negative and the two disagree out loud.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var fund = account.FundId("Bank");
        var me = account.AddMember(Guid.NewGuid(), "Me");
        var insurance = account.AddSavingCategory("Insurance");

        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(me.UserId, M(2000), fundId: fund);
        p.AllocateToSavings(insurance.Id, M(300), new DateOnly(2026, 1, 5));
        // The sinking fund pays the bill it had been filling for a year — more leaves than went in this month.
        p.DisburseSaving(insurance.Id, fund, M(500), new DateOnly(2026, 1, 20), "Insurance");

        Assert.True(p.SavingsNetTotal.Amount < 0m);        // the money model is net-negative...
        Assert.Equal(M(300), p.SavingsSetAsideTotal);      // ...but €300 was genuinely set aside

        var report = new InsightsService().Build(account, 0);

        Assert.DoesNotContain(report.Signals, s => s.Title.Code == InsightCodes.SigNoSavingsTitle);
        Assert.DoesNotContain(report.SavingsCritique, m => m.Code == InsightCodes.CritNoneYet);
    }

    [Fact]
    public void Saving_out_of_carried_over_cash_names_the_amount_instead_of_claiming_there_is_nothing_to_measure()
    {
        // No income at all this period, so the savings RATE has no denominator and is null. That is not the same
        // as saving nothing, and the savings paragraph must not read as though it were.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food").Id;
        var fund = account.FundId("Bank");
        var laptop = account.AddSavingCategory("New laptop");

        var p = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        p.AddExpense(new Expense(food, M(50), new DateOnly(2026, 2, 3), Guid.NewGuid(), fund));  // gives the report data
        p.AllocateToSavings(laptop.Id, M(420), new DateOnly(2026, 2, 2));

        var report = new InsightsService().Build(account, 0);

        Assert.Equal(InsightCodes.CritAsideNoIncome, report.SavingsCritique[0].Code);
        Assert.Equal(InsightArgKind.Money, report.SavingsCritique[0].Args[0].Kind);
        Assert.DoesNotContain(report.SavingsCritique, m => m.Code == InsightCodes.CritNoContrib);
    }
}
