using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;


namespace FinApp.Server.Tests;

/// <summary>
/// The debt-payoff read — the forecast figures a thin client cannot compute for itself.
/// <para>
/// This read exists so Android never has to: it holds the loan's inputs and no amortisation, and a payoff date is
/// the kind of number that looks entirely plausible when it is wrong. These tests pin what the wire carries, and in
/// particular the two states the client must render differently — a loan with a schedule, and one without.
/// </para>
/// </summary>
public class DebtPayoffApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public DebtPayoffApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>A €10,000 loan at 6% with a €300 installment, in an open Jan-2026 period.</summary>
    private static async Task<Guid> SeedLoanAsync(HttpClient client, Guid accountId, Guid memberId,
        decimal installment = 300m)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var bucket = agg.AddSavingCategory("Car loan");
        bucket.ConfigureDebt(10_000m, 6m, installment, originalBalance: 10_000m,
            balanceAsOf: new DateOnly(2026, 1, 1));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return bucket.Id;
    }

    private static Task<DebtPayoffDto?> Payoff(HttpClient client, Guid accountId, Guid bucketId) =>
        client.GetFromJsonAsync<DebtPayoffDto>($"/accounts/{accountId}/savings/{bucketId}/payoff");

    [Fact]
    public async Task A_loan_with_a_schedule_reports_when_it_ends_and_what_it_costs()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dp_basic");
        var account = await CreateAccount(client, "Debt");
        var bucket = await SeedLoanAsync(client, account.Id, auth.UserId);

        var p = (await Payoff(client, account.Id, bucket))!;

        Assert.True(p.Available);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(10_000m, p.Balance);
        Assert.Equal(300m, p.Installment);
        Assert.True(p.Months > 0);
        Assert.True(p.TotalInterest > 0m);
        // Counted from the period we are looking at, not from "now" — the rest of this read is period-scoped.
        Assert.Equal(new DateOnly(2026, 1, 1).AddMonths(p.Months), p.PayoffOn);
    }

    [Fact]
    public async Task A_loan_with_no_workable_schedule_says_so_instead_of_inventing_a_date()
    {
        // An installment that cannot out-run the monthly interest never clears the loan. €10,000 at 6% accrues
        // €50 in the first month alone, so €20 a month goes backwards forever. The honest answer is "we can't
        // say" — a client drawing a payoff date here would be stating something that will never happen.
        var (client, auth) = await _factory.RegisterAndAuthAsync("dp_noschedule");
        var account = await CreateAccount(client, "Debt");
        var bucket = await SeedLoanAsync(client, account.Id, auth.UserId, installment: 20m);

        var p = (await Payoff(client, account.Id, bucket))!;

        Assert.False(p.Available);
        Assert.Equal(0, p.Months);
        Assert.Null(p.PayoffOn);
        // The inputs still come back, so the screen can show the loan itself rather than nothing at all.
        Assert.Equal(10_000m, p.Balance);
    }

    [Fact]
    public async Task A_bucket_that_is_not_a_debt_returns_nothing_rather_than_erroring()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("dp_notadebt");
        var account = await CreateAccount(client, "Debt");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var goal = agg.AddSavingCategory("Holiday");   // an ordinary bucket — never configured as a debt
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var p = (await Payoff(client, account.Id, goal.Id))!;
        Assert.False(p.Available);
    }

    [Fact]
    public async Task Stranger_cannot_read_a_payoff()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("dp_owner");
        var account = await CreateAccount(owner, "Debt");
        var bucket = await SeedLoanAsync(owner, account.Id, auth.UserId);

        var (stranger, _) = await _factory.RegisterAndAuthAsync("dp_stranger");
        var resp = await stranger.GetAsync($"/accounts/{account.Id}/savings/{bucket}/payoff");
        Assert.False(resp.IsSuccessStatusCode);
    }
}
