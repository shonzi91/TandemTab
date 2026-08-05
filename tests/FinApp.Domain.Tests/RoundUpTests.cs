using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>F4 — round each expense up to the next whole 1 or 5 and set the change aside.</summary>
public class RoundUpTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    private static (Account account, Period period, Category food, SavingCategory jar, Guid member) Setup(decimal contributed = 1000m)
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");
        var jar = account.AddSavingCategory("Spare change");
        account.AddDefaultFunds();
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(contributed));
        return (account, period, food, jar, member.UserId);
    }

    private static Expense Spend(Account account, Period period, Category food, Guid member, decimal amount)
    {
        var expense = new Expense(food.Id, M(amount), Jan5, member, account.FundId("Cash"));
        period.AddExpense(expense);
        return expense;
    }

    [Fact]
    public void Round_ups_are_off_on_a_new_account_and_sweep_nothing()
    {
        var (account, period, food, _, member) = Setup();
        Assert.False(account.RoundUpsOn);
        Assert.Equal(0m, account.RoundUpFor(12.40m));

        var expense = Spend(account, period, food, member, 12.40m);
        Assert.Null(new RoundUpService().Sweep(account, period, expense.Amount, expense.Date));
        Assert.Empty(period.SavingAllocations);
    }

    [Theory]
    [InlineData(1, 12.40, 0.60)]
    [InlineData(1, 12.01, 0.99)]
    [InlineData(5, 12.40, 2.60)]
    [InlineData(5, 2.00, 3.00)]
    public void The_change_is_the_distance_up_to_the_next_step(decimal step, decimal amount, decimal expected)
    {
        var (account, _, _, jar, _) = Setup();
        account.ConfigureRoundUps(step, jar.Id);
        Assert.Equal(expected, account.RoundUpFor(amount));
    }

    [Theory]
    [InlineData(1, 12.00)]
    [InlineData(5, 15.00)]
    public void An_amount_already_on_the_step_sweeps_nothing(decimal step, decimal amount)
    {
        var (account, period, food, jar, member) = Setup();
        account.ConfigureRoundUps(step, jar.Id);

        var expense = Spend(account, period, food, member, amount);
        Assert.Null(new RoundUpService().Sweep(account, period, expense.Amount, expense.Date));
        Assert.Empty(period.SavingAllocations);
    }

    [Fact]
    public void A_sweep_sets_the_change_aside_in_the_chosen_bucket_without_touching_the_expense()
    {
        var (account, period, food, jar, member) = Setup();
        account.ConfigureRoundUps(1m, jar.Id);

        var expense = Spend(account, period, food, member, 12.40m);
        var swept = new RoundUpService().Sweep(account, period, expense.Amount, expense.Date);

        Assert.NotNull(swept);
        Assert.Equal(jar.Id, swept!.SavingCategoryId);
        Assert.Equal(M(0.60m), swept.Amount);
        Assert.Equal(RoundUpService.SweepNote, swept.Note);
        // The ledger still records exactly what was spent — the round-up is an earmark, not a second expense.
        Assert.Equal(M(12.40m), period.ExpensesTotal);
        Assert.Single(period.Expenses);
    }

    [Fact]
    public void The_change_is_reserved_so_free_cash_drops_by_the_expense_plus_the_round_up()
    {
        var (account, period, food, jar, member) = Setup(contributed: 100m);
        account.ConfigureRoundUps(1m, jar.Id);

        var expense = Spend(account, period, food, member, 12.40m);
        new RoundUpService().Sweep(account, period, expense.Amount, expense.Date);

        // 100 − 12.40 spent − 0.60 earmarked = 87.00
        Assert.Equal(M(87.00m), period.FreeToAllocateAfter(M(0)));
    }

    [Fact]
    public void A_sweep_that_would_outrun_the_cash_is_skipped_rather_than_driving_free_negative()
    {
        // Spend everything: no headroom is left for the change, and an automatic move must not raise the
        // "overspent into savings" alarm over a few cents nobody chose to set aside.
        var (account, period, food, jar, member) = Setup(contributed: 12.40m);
        account.ConfigureRoundUps(1m, jar.Id);

        var expense = Spend(account, period, food, member, 12.40m);
        Assert.Null(new RoundUpService().Sweep(account, period, expense.Amount, expense.Date));
        Assert.Empty(period.SavingAllocations);
        Assert.Equal(M(0m), period.FreeToAllocateAfter(M(0)));
    }

    [Fact]
    public void An_archived_destination_bucket_stops_the_sweep_without_throwing()
    {
        var (account, period, food, jar, member) = Setup();
        account.ConfigureRoundUps(1m, jar.Id);
        account.SetSavingArchived(jar.Id, true);

        Assert.False(account.RoundUpsOn);
        var expense = Spend(account, period, food, member, 12.40m);
        Assert.Null(new RoundUpService().Sweep(account, period, expense.Amount, expense.Date));
    }

    [Fact]
    public void Only_a_step_of_0_1_or_5_is_accepted()
    {
        var (account, _, _, jar, _) = Setup();
        Assert.Throws<ArgumentOutOfRangeException>(() => account.ConfigureRoundUps(2m, jar.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.ConfigureRoundUps(0.5m, jar.Id));
    }

    [Fact]
    public void Turning_round_ups_on_requires_a_bucket_that_exists()
    {
        var (account, _, _, _, _) = Setup();
        Assert.Throws<InvalidOperationException>(() => account.ConfigureRoundUps(1m, null));
        Assert.Throws<InvalidOperationException>(() => account.ConfigureRoundUps(1m, Guid.NewGuid()));
        Assert.False(account.RoundUpsOn);
    }

    [Fact]
    public void Removing_the_destination_bucket_switches_round_ups_off()
    {
        var (account, _, _, jar, _) = Setup();
        account.ConfigureRoundUps(5m, jar.Id);

        account.RemoveSavingCategory(jar.Id);   // allowed: no savings activity against it

        Assert.Equal(0m, account.RoundUpTo);
        Assert.Null(account.RoundUpBucketId);
    }

    [Fact]
    public void The_configuration_survives_a_snapshot_round_trip_and_legacy_accounts_restore_off()
    {
        var (account, _, _, jar, _) = Setup();
        account.ConfigureRoundUps(5m, jar.Id);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(5m, restored.RoundUpTo);
        Assert.Equal(jar.Id, restored.RoundUpBucketId);
        Assert.True(restored.RoundUpsOn);

        var legacy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(new Account("Plain", Eur)));
        Assert.Equal(0m, legacy.RoundUpTo);
        Assert.False(legacy.RoundUpsOn);
    }
}
