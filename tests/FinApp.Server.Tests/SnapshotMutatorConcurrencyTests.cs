using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Server.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// The concurrency contract of the mutation spine (<see cref="SnapshotService.MutateAsync{T}"/>): a write that loses
/// a race to a concurrent writer is not lost or clobbered — the Version concurrency token makes the losing UPDATE
/// throw, and MutateAsync reloads the winner's state and re-applies the mutation on top of it. Driven deterministically
/// by injecting exactly one competing write from inside the mutate delegate's first pass.
/// </summary>
public class SnapshotMutatorConcurrencyTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SnapshotMutatorConcurrencyTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Category, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var category = agg.AddCategory("Food").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: fund);
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (category, fund);
    }

    [Fact]
    public async Task Mutation_retries_and_merges_when_a_concurrent_write_lands_mid_call()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("exp_race");
        var account = await CreateAccount(client, "Race");
        var (cat, fund) = await SeedAsync(client, account.Id, auth.UserId);   // snapshot now at v1

        var passes = 0;
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SnapshotService>();

        var (version, _) = await svc.MutateAsync(auth.UserId, account.Id, acc =>
        {
            // On the first pass only, a competing writer (its own scope + DbContext) commits expense B, bumping the
            // row to v2 — so our save, which expects v1, loses the race and forces exactly one reload + retry.
            if (Interlocked.Increment(ref passes) == 1)
            {
                using var other = _factory.Services.CreateScope();
                other.ServiceProvider.GetRequiredService<SnapshotService>()
                    .MutateAsync(auth.UserId, account.Id, a =>
                    {
                        a.CurrentPeriod!.AddExpense(new Expense(cat, new Money(11m, "EUR"), When, auth.UserId, fund, "B"));
                        return 0;
                    }).GetAwaiter().GetResult();
            }
            acc.CurrentPeriod!.AddExpense(new Expense(cat, new Money(22m, "EUR"), When, auth.UserId, fund, "A"));
            return 0;
        });

        Assert.Equal(2, passes);     // the delegate ran twice: first conflicted, second succeeded
        Assert.Equal(3, version);    // v1 seed → v2 competitor → v3 us

        // Both survived: the competitor's B (11) and our A (22) — nothing clobbered.
        var ov = (await client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{account.Id}/overview"))!;
        Assert.Equal(33m, ov.Spent);
    }
}
