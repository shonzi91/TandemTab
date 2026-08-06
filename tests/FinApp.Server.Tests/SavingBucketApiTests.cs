using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Savings;

namespace FinApp.Server.Tests;

/// <summary>
/// Savings-bucket CRUD/config (POST/PUT/DELETE /accounts/{id}/savings/buckets + .../archived), mirroring
/// <c>BudgetingState.AddSavingBucket/SaveSavingBucket</c>. Config is verified by reading the snapshot back and
/// inspecting the <see cref="SavingCategory"/> — the config surface (goal/debt/investment/expenses-fund) isn't in a
/// computed read.
/// </summary>
public class SavingBucketApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SavingBucketApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed an initialized snapshot (funds + one period, no buckets). Returns the "Bank" fund id. v1.</summary>
    private static async Task<Guid> SeedAsync(HttpClient client, Guid accountId, Guid memberId, int periods = 1)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        for (var i = 1; i < periods; i++)
            agg.StartPeriod(new DateOnly(2026, 1 + i, 1), new DateOnly(2026, 1 + i, 28));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return fund;
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId)
    {
        var snap = (await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!;
        return AccountSnapshotSerializer.Deserialize(snap.Payload);
    }

    [Fact]
    public async Task Create_goal_bucket_sets_name_icon_goal_and_threshold()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_goal");
        var account = await CreateAccount(client, "Data");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Holiday", Icon: "beach", GoalAmount: 2000m, ThresholdPercent: 90m,
                NotifyOnMilestone: true, InitialAmount: 150m, FundId: fund));
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!;
        Assert.Equal(2, result.Version);

        var bucket = (await LoadAsync(client, account.Id)).FindSavingCategory(result.EntityId!.Value)!;
        Assert.Equal("Holiday", bucket.Name);
        Assert.Equal("beach", bucket.Icon);
        Assert.Equal(2000m, bucket.GoalAmount);
        Assert.Equal(0.90m, bucket.AlertThreshold);
        Assert.True(bucket.NotifyOnMilestone);
        Assert.Equal(fund, bucket.FundId);
        Assert.Equal(150m, bucket.InitialAmount);   // honoured: single period
        Assert.False(bucket.IsDebt);
    }

    [Fact]
    public async Task Create_debt_bucket_sets_debt_figures_and_no_goal()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_debt");
        var account = await CreateAccount(client, "Debt");
        await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6.5m, DebtInstallment: 300m));
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var bucket = (await LoadAsync(client, account.Id)).FindSavingCategory(id)!;
        Assert.True(bucket.IsDebt);
        Assert.Equal(8000m, bucket.DebtBalance);
        Assert.Equal(6.5m, bucket.DebtAnnualRatePercent);
        Assert.Equal(300m, bucket.DebtInstallment);
        Assert.False(bucket.HasGoal);
    }

    [Fact]
    public async Task Update_switches_a_goal_bucket_to_an_expenses_fund_and_clears_the_goal()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_switch");
        var account = await CreateAccount(client, "Switch");
        await SeedAsync(client, account.Id, auth.UserId);

        var id = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car", GoalAmount: 1000m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var costs = new[] { new PlannedCostDto("Insurance", 400m, "yearly"), new PlannedCostDto("Tyres", 600m, "one-off", new DateOnly(2026, 12, 1)) };
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{id}",
            new SaveSavingBucketRequest("Car costs", IsExpensesFund: true, Costs: costs))).EnsureSuccessStatusCode();

        var bucket = (await LoadAsync(client, account.Id)).FindSavingCategory(id)!;
        Assert.Equal("Car costs", bucket.Name);
        Assert.True(bucket.IsExpensesFund);
        Assert.False(bucket.HasGoal);   // the goal was cleared by the sinking-fund switch
    }

    [Fact]
    public async Task Initial_amount_is_ignored_once_the_account_has_more_than_one_period()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_initial");
        var account = await CreateAccount(client, "Initial");
        await SeedAsync(client, account.Id, auth.UserId, periods: 2);   // two periods → setup window closed

        var id = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Rainy", GoalAmount: 500m, InitialAmount: 999m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var bucket = (await LoadAsync(client, account.Id)).FindSavingCategory(id)!;
        Assert.Equal(0m, bucket.InitialAmount);
    }

    [Fact]
    public async Task Archive_and_restore_a_bucket()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_arch");
        var account = await CreateAccount(client, "Arch");
        await SeedAsync(client, account.Id, auth.UserId);

        var id = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Old goal", GoalAmount: 100m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{id}/archived", new SetArchivedRequest(true))).EnsureSuccessStatusCode();
        Assert.True((await LoadAsync(client, account.Id)).FindSavingCategory(id)!.IsArchived);

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{id}/archived", new SetArchivedRequest(false))).EnsureSuccessStatusCode();
        Assert.False((await LoadAsync(client, account.Id)).FindSavingCategory(id)!.IsArchived);
    }

    [Fact]
    public async Task Remove_an_unused_bucket()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_rm");
        var account = await CreateAccount(client, "Rm");
        await SeedAsync(client, account.Id, auth.UserId);

        var id = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Temp", GoalAmount: 100m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        (await client.DeleteAsync($"/accounts/{account.Id}/savings/buckets/{id}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindSavingCategory(id));
    }

    [Fact]
    public async Task Remove_is_blocked_when_the_bucket_has_savings_activity()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_rmblock");
        var account = await CreateAccount(client, "RmBlock");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        var id = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Active", GoalAmount: 100m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
        // Give it activity via the savings-deposit endpoint.
        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/deposits",
            new AddSavingDepositRequest(id, 50m, new DateOnly(2026, 1, 10)))).EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/accounts/{account.Id}/savings/buckets/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_with_a_duplicate_name_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_dup");
        var account = await CreateAccount(client, "Dup");
        await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Rainy day", GoalAmount: 100m))).EnsureSuccessStatusCode();
        var dup = await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Rainy day", GoalAmount: 200m));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    [Fact]
    public async Task Savings_view_carries_the_raw_forecast_inputs_for_client_side_whatif_reprojection()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_forecast");
        var account = await CreateAccount(client, "Forecast");
        await SeedAsync(client, account.Id, auth.UserId);

        var debtId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6.5m,
                DebtInstallment: 300m, PlannedContribution: 350m))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
        var invId = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Index fund", IsInvestment: true, InvRate: 5m, InvTermYears: 10m,
                InvCompounds: 4))).Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;

        var view = (await client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{account.Id}/savings"))!;

        var debt = view.Buckets.Single(b => b.Id == debtId);
        Assert.NotNull(debt.Forecast);
        Assert.Equal(8000m, debt.Forecast!.DebtStoredBalance);
        Assert.Equal(8000m, debt.Forecast.DebtOriginalBalance);   // original defaults to the opening balance
        Assert.Equal(6.5m, debt.Forecast.DebtRatePercent);
        Assert.Equal(300m, debt.Forecast.DebtInstallment);
        Assert.Equal(350m, debt.Forecast.PlannedContribution);
        Assert.Null(debt.Forecast.InvestmentRatePercent);         // debt carries no investment knobs

        var inv = view.Buckets.Single(b => b.Id == invId);
        Assert.NotNull(inv.Forecast);
        Assert.Equal(5m, inv.Forecast!.InvestmentRatePercent);
        Assert.Equal(10m, inv.Forecast.InvestmentTermYears);
        Assert.Equal(4, inv.Forecast.InvestmentCompoundsPerYear);
        Assert.Null(inv.Forecast.DebtStoredBalance);              // investment carries no debt knobs
    }

    /// <summary>
    /// The R2 edit-form prefill. <c>SaveSavingBucketRequest</c> is a full OVERWRITE, so a thin client has to read
    /// back every field it doesn't edit and send it again. These four were invisible in the read model, which meant
    /// a native edit silently cleared the held-in fund, reset the alert threshold to 80%, switched milestone
    /// notification off, and wiped the starting balance. They are asserted together because they share one cause.
    /// </summary>
    [Fact]
    public async Task Savings_read_exposes_the_fields_the_upsert_would_otherwise_clear()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_prefill");
        var account = await CreateAccount(client, "Data");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Holiday", GoalAmount: 2000m, ThresholdPercent: 65m,
                NotifyOnMilestone: true, InitialAmount: 150m, FundId: fund))).EnsureSuccessStatusCode();

        var view = (await client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{account.Id}/savings"))!;
        var bucket = view.Buckets.Single();
        Assert.Equal(fund, bucket.FundId);
        Assert.Equal(65m, bucket.ThresholdPercent);   // sent as a percentage, not the domain's 0..1 fraction
        Assert.True(bucket.NotifyOnMilestone);
        Assert.Equal(150m, bucket.InitialAmount);
    }

    /// <summary>An edit rebuilt purely from what the read model reports must be a no-op for everything the form
    /// didn't touch — the actual round-trip a native edit performs.</summary>
    [Fact]
    public async Task Edit_rebuilt_from_the_read_model_preserves_fund_threshold_notify_and_initial()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("bk_roundtrip");
        var account = await CreateAccount(client, "Data");
        var fund = await SeedAsync(client, account.Id, auth.UserId);

        var created = (await (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Holiday", GoalAmount: 2000m, ThresholdPercent: 65m,
                NotifyOnMilestone: true, InitialAmount: 150m, FundId: fund)))
            .Content.ReadFromJsonAsync<MutationResultDto>())!;
        var bucketId = created.EntityId!.Value;

        var before = (await client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{account.Id}/savings"))!.Buckets.Single();

        // Rename only — every other field comes straight back from the read, as the sheet does.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{bucketId}",
            new SaveSavingBucketRequest("Holiday 2027", GoalAmount: before.GoalTarget,
                ThresholdPercent: before.ThresholdPercent, NotifyOnMilestone: before.NotifyOnMilestone,
                InitialAmount: before.InitialAmount, FundId: before.FundId))).EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{account.Id}/savings"))!.Buckets.Single();
        Assert.Equal("Holiday 2027", after.Name);
        Assert.Equal(fund, after.FundId);
        Assert.Equal(65m, after.ThresholdPercent);
        Assert.True(after.NotifyOnMilestone);
        Assert.Equal(150m, after.InitialAmount);
        Assert.Equal(2000m, after.GoalTarget);
    }

    [Fact]
    public async Task Stranger_cannot_create_a_bucket()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("bk_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("bk_intruder");
        var account = await CreateAccount(owner, "Private");
        await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Sneaky", GoalAmount: 100m));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
