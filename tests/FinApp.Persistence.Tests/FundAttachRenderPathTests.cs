using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Persistence.Tests;

// Regression guard from the Session-26 fund-attach "freeze" investigation: a starter account with a second
// savings bucket earmarked to a fund must round-trip and run the full per-bucket render-path (SavingsReportService
// reads + descendant walks) without hanging or throwing. Proves the domain/serializer layer can't be the cause of
// a render freeze; the timeout catches any unbounded loop (e.g. a cyclic parent chain) that slips back in.
public class FundAttachRenderPathTests
{
    [Fact]
    public async Task Fund_attached_bucket_render_path_does_not_hang_or_throw()
    {
        var work = Task.Run(RunRenderPath);
        var done = await Task.WhenAny(work, Task.Delay(8000));
        Assert.True(done == work, "Render-path computation hung (>8s) — the freeze is a domain loop.");
        await work; // surface any exception with its stack
    }

    private static void RunRenderPath()
    {
        var account = new Account("Personal", "EUR");
        account.AssignOwner(Guid.NewGuid(), "testuser");
        foreach (var (name, icon) in new[] { ("Food", "🍽️"), ("Bills", "💡"), ("Transport", "🚗"), ("Other", "🏷️") })
            account.AddCategory(name, icon: icon);
        account.AddSavingCategory("General");
        foreach (var c in new[] { "Salary", "Other" })
            account.AddContributionCategory(c);
        account.AddDefaultFunds();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        var period = account.StartPeriod(from, from.AddMonths(1).AddDays(-1));

        // The user's action: a second bucket earmarked to the Bank fund.
        var car = account.AddSavingCategory("Car");
        account.SetSavingFund(car.Id, account.FundId("Bank"));

        account.SetAchievementsAnchor(from);
        account.RecordAchievement("first_bucket", today);

        // Round-trip like the app does on every save/load.
        var roundTripped = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        // Run the per-bucket render-path domain reads for every bucket (this is what SavingBuckets / the goal cards do).
        var savings = new SavingsReportService();
        foreach (var acct in new[] { account, roundTripped })
        {
            var p = acct.Periods[^1];
            _ = savings.AccumulatedTotal(acct);
            _ = savings.AccountSavingsRate(acct);
            _ = savings.PeriodSavingsRate(p);
            foreach (var b in acct.SavingCategories)
            {
                _ = savings.ForBucket(acct, p, b.Id);
                _ = savings.GoalProgress(acct, b.Id);
                _ = savings.AverageDepositPace(acct, b.Id);
                _ = savings.DebtBalanceHistory(acct, b.Id);
                _ = acct.SavingCategoryWithDescendantIds(b.Id);
            }
        }
    }
}
