using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FinApp.Persistence.Tests;

public class AccountRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"finapp-test-{Guid.NewGuid():N}.db");
    private const string Password = "round-trip-key";

    [Fact]
    public async Task Full_aggregate_survives_save_and_reload()
    {
        var (accountId, vacationsId, savingExpenseId) = await SeedAndSaveAsync();

        await using var ctx = new FinAppDbContext(FinAppDb.BuildOptions(_dbPath, Password));
        var store = new AccountStore(ctx);
        var account = await store.LoadFirstAsync();

        Assert.NotNull(account);
        Assert.Equal(accountId, account!.Id);
        Assert.Equal("Family", account.Name);
        Assert.Equal(2, account.Members.Count);

        // Four categories, all top-level: nesting is gone, and a parent id passed by an older caller is ignored
        // rather than rejected (see CategoryFlattenTests for what happens to trees already in the wild).
        Assert.Equal(4, account.Categories.Count);
        Assert.All(account.Categories, c => Assert.Null(c.ParentId));

        var period = Assert.Single(account.Periods);
        Assert.Equal(new Money(1250, "EUR"), period.InitialTotal);     // 1100 bank + 150 cash
        Assert.Equal(new Money(1500, "EUR"), period.ContributionsPaidTotal); // 1500 paid (partner owes rest)
        Assert.Equal(new Money(220, "EUR"), period.ExpensesTotal);     // 100 food + 120 saving-funded
        Assert.Equal(new Money(180, "EUR"), period.SavingsNetTotal);   // 300 allocated - 120 drawn down

        // The saving -> expense conversion link is preserved.
        var savingExpense = Assert.Single(period.Expenses, e => e.Id == savingExpenseId);
        Assert.Equal(vacationsId, savingExpense.SourceSavingCategoryId);
        Assert.True(savingExpense.IsFromSavings);

        // Only the depositing member has a contribution row.
        Assert.Single(period.Contributions);
    }

    [Fact]
    public async Task Mutating_and_saving_again_persists_new_expense()
    {
        var (_, _, _) = await SeedAndSaveAsync();

        await using (var ctx = new FinAppDbContext(FinAppDb.BuildOptions(_dbPath, Password)))
        {
            var store = new AccountStore(ctx);
            var account = await store.LoadFirstAsync();
            var period = account!.Periods.Single();
            var food = account.Categories.First(c => c.Name == "Food");
            period.AddExpense(new Expense(food.Id, new Money(40, "EUR"),
                period.From.AddDays(11), account.Members.First().UserId, Guid.NewGuid(), "Snacks"));
            await store.SaveAsync();
        }

        await using var verify = new FinAppDbContext(FinAppDb.BuildOptions(_dbPath, Password));
        var reloaded = await new AccountStore(verify).LoadFirstAsync();
        Assert.Equal(new Money(260, "EUR"), reloaded!.Periods.Single().ExpensesTotal); // 220 + 40
    }

    [Fact]
    public async Task Wrong_password_cannot_open_the_encrypted_database()
    {
        await SeedAndSaveAsync();

        await using var ctx = new FinAppDbContext(FinAppDb.BuildOptions(_dbPath, "wrong-key"));
        await Assert.ThrowsAnyAsync<SqliteException>(async () => await new AccountStore(ctx).LoadFirstAsync());
    }

    private async Task<(Guid AccountId, Guid VacationsId, Guid SavingExpenseId)> SeedAndSaveAsync()
    {
        await using var ctx = new FinAppDbContext(FinAppDb.BuildOptions(_dbPath, Password));
        var store = new AccountStore(ctx);
        store.Migrate();

        var account = new Account("Family", "EUR");
        var stoyan = account.AddMember(Guid.NewGuid(), "Stoyan");
        var partner = account.AddMember(Guid.NewGuid(), "Partner");

        var food = account.AddCategory("Food");
        account.AddCategory("Restaurants", food.Id);
        var kids = account.AddCategory("Kids");
        account.AddCategory("Kid1", kids.Id);
        var vacations = account.AddSavingCategory("Vacations");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(Guid.NewGuid(), new Money(1100, "EUR"));
        period.SetInitialBalance(Guid.NewGuid(), new Money(150, "EUR"));
        period.Deposit(stoyan.UserId, new Money(1500, "EUR"));
        // partner makes no deposit this period
        period.AddBudget(food.Id, new Money(600, "EUR"), 0.80m, notifyOnEveryExpense: true);
        period.AddExpense(new Expense(food.Id, new Money(100, "EUR"),
            new DateOnly(2026, 1, 5), stoyan.UserId, Guid.NewGuid(), "Groceries"));
        period.AllocateToSavings(vacations.Id, new Money(300, "EUR"), new DateOnly(2026, 1, 2));
        var savingExpense = period.ConvertSavingToExpense(vacations.Id, food.Id, new Money(120, "EUR"),
            new DateOnly(2026, 1, 20), stoyan.UserId, Guid.NewGuid(), "Holiday dinner");

        await store.AddAsync(account);
        return (account.Id, vacations.Id, savingExpense.Id);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
    }
}
