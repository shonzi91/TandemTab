using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

/// <summary>
/// The emergency fund is the one bucket whose goal the app derives rather than asks for: three months of what the
/// essential categories actually cost, rounded up to 500. These pin the two decisions that are not obvious — that
/// the monthly figure comes from COMPLETED periods rather than the running one, and that the app declines to invent
/// a target when nothing is marked essential.
/// </summary>
public class EmergencyFundTests
{
    private const string Eur = "EUR";

    private static Account Seed(out Guid rent, out Guid fun)
    {
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var r = account.AddCategory("Rent");
        var f = account.AddCategory("Fun");
        account.SetCategoryEssential(r.Id, true);
        rent = r.Id;
        fun = f.Id;
        return account;
    }

    private static void Spend(Period period, Account account, Guid categoryId, decimal amount) =>
        period.AddExpense(new Expense(categoryId, new Money(amount, Eur), period.From, Guid.NewGuid(), account.FundId("Bank")));

    [Fact]
    public void The_target_is_three_months_of_essentials_rounded_up_to_500()
    {
        var account = Seed(out var rent, out var fun);
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(p, account, rent, 900m);
        Spend(p, account, fun, 400m);        // not essential — must not count
        p.Close();

        // 900 × 3 = 2700 → rounded up to the next 500 = 3000.
        Assert.Equal(3000m, account.EmergencyFundTarget());
    }

    [Fact]
    public void The_stated_basis_is_the_real_spend_not_the_rounded_target_divided_back()
    {
        // ★ The rounding is one-way. 900 → a 3000 target, and 3000 / 3 is 1000 — a monthly figure the user never
        // spent. Anything reporting the basis must read it from EssentialSpendPerPeriod.
        var account = Seed(out var rent, out _);
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(p, account, rent, 900m);
        p.Close();

        Assert.Equal(900m, account.EssentialSpendPerPeriod());
        Assert.NotEqual(account.EssentialSpendPerPeriod(), account.EmergencyFundTarget() / 3m);
    }

    [Fact]
    public void Spending_under_a_flattened_sub_category_still_counts_as_essential()
    {
        // This used to be inheritance: a sub-category counted because its parent was essential. Sub-categories are
        // gone, and the property survives for a better reason — the spend now sits ON the essential category.
        var account = Seed(out var rent, out _);
        var sub = account.AddCategory("Utilities").Id;
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(p, account, sub, 600m);
        p.Close();

        var loaded = CategoryFlattenTests.LoadAsLegacy(account, "Utilities", rent);

        Assert.Equal(2000m, loaded.EmergencyFundTarget());   // 600 × 3 = 1800 → 2000
    }

    [Fact]
    public void The_monthly_figure_averages_completed_periods_not_the_open_one()
    {
        // ★ The reason this isn't the literal "sum of essential expenses": mid-month that sum is near zero, so the
        // target would start tiny and climb all month — a goal that grows as you spend is not a goal.
        var account = Seed(out var rent, out _);
        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(jan, account, rent, 1000m);
        jan.Close();
        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        Spend(feb, account, rent, 500m);
        feb.Close();
        var mar = account.StartPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        Spend(mar, account, rent, 10m);      // two days in — must not drag the target down

        // Average of the two CLOSED periods = 750; 750 × 3 = 2250 → 2500.
        Assert.Equal(2500m, account.EmergencyFundTarget());
    }

    [Fact]
    public void With_no_history_the_open_period_is_used_rather_than_nothing()
    {
        var account = Seed(out var rent, out _);
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(p, account, rent, 700m);

        Assert.Equal(2500m, account.EmergencyFundTarget());   // 2100 → 2500
    }

    [Fact]
    public void No_essential_categories_means_no_target_rather_than_a_guess()
    {
        // Deriving from total spending would quietly redefine "essential" as "everything", which is the one number
        // an emergency fund must not be built on.
        var account = new Account("Personal", Eur);
        account.AddDefaultFunds();
        var cat = account.AddCategory("Fun").Id;
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Spend(p, account, cat, 800m);
        p.Close();

        Assert.Null(account.EmergencyFundTarget());
    }

    [Fact]
    public void Only_one_bucket_can_be_the_emergency_fund()
    {
        var account = Seed(out _, out _);
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var first = account.AddSavingCategory("Rainy day").Id;
        var second = account.AddSavingCategory("Buffer").Id;

        account.SetEmergencyFund(first, true);
        Assert.Equal(first, account.EmergencyFund!.Id);

        account.SetEmergencyFund(second, true);
        Assert.Equal(second, account.EmergencyFund!.Id);
        Assert.False(account.FindSavingCategory(first)!.IsEmergencyFund);   // the label moved, it didn't duplicate

        account.SetEmergencyFund(second, false);
        Assert.Null(account.EmergencyFund);
    }

    [Fact]
    public void A_debt_bucket_cannot_be_the_emergency_fund()
    {
        var account = Seed(out _, out _);
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var loan = account.AddSavingCategory("Car loan").Id;
        account.ConfigureSavingDebt(loan, 8000m, 6m, 400m);

        account.SetEmergencyFund(loan, true);

        Assert.Null(account.EmergencyFund);   // a debt is not a cushion
    }

    [Fact]
    public void The_flag_survives_a_snapshot_round_trip()
    {
        var account = Seed(out _, out _);
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var bucket = account.AddSavingCategory("Rainy day").Id;
        account.SetEmergencyFund(bucket, true);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Equal(bucket, restored.EmergencyFund!.Id);
    }
}
