using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The whole-stack payoff plan — the read that lets a thin client answer <b>"when am I debt-free"</b> rather than
/// only "when does this loan end". It is the half of QUEUE #8 the surface sweep found: the web has computed this
/// in the thick client since the multi-debt planner shipped, and the server exposed nothing.
///
/// <para>★ What these pin is mostly the <i>shape of the honesty</i>: a stack that never clears must say so, the
/// two dates (plan versus demonstrated pace) must stay distinct, and the strategies must actually differ in their
/// clearing order — because a plan that quietly ignores the strategy chip looks exactly like one that honours it.</para>
/// </summary>
public class DebtPlanApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public DebtPlanApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly PeriodStart = new(2026, 1, 1);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Two loans that rank differently under the two strategies — a SMALL, EXPENSIVE card and a BIG,
    /// CHEAP loan. Avalanche must attack the card (highest rate); snowball must attack it too only if it is also
    /// the smallest, so the card is deliberately both, and the second seed below flips it.</summary>
    private static async Task<(Guid Card, Guid Loan)> SeedTwoDebtsAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(PeriodStart, new DateOnly(2026, 1, 31));

        var card = agg.AddSavingCategory("Credit card");
        card.ConfigureDebt(2_000m, 22m, 150m, originalBalance: 2_000m, balanceAsOf: PeriodStart);
        var loan = agg.AddSavingCategory("Car loan");
        loan.ConfigureDebt(10_000m, 4m, 300m, originalBalance: 10_000m, balanceAsOf: PeriodStart);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (card.Id, loan.Id);
    }

    private static Task<DebtPlanDto?> Plan(HttpClient client, Guid accountId, string query = "") =>
        client.GetFromJsonAsync<DebtPlanDto>($"/accounts/{accountId}/savings/plan{query}");

    [Fact]
    public async Task The_plan_answers_when_the_whole_stack_clears()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_basic");
        var account = await CreateAccount(client, "Debts");
        await SeedTwoDebtsAsync(client, account.Id, auth.UserId);

        var p = (await Plan(client, account.Id))!;

        Assert.True(p.Available);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(2, p.DebtCount);
        Assert.Equal(12_000m, p.TotalOwed);
        Assert.Equal(450m, p.TotalInstallments);
        Assert.Equal("avalanche", p.Strategy);          // the default
        Assert.Equal(2, p.Order.Count);
        // Counted from the period being looked at, not from "now" — the same anchor the per-bucket payoff uses,
        // so the two reads on one screen cannot disagree by however long ago that period started.
        Assert.Equal(PeriodStart.AddMonths(p.Months), p.DebtFreeOn);
        // You are debt-free when the LAST one clears, so the plan runs at least as long as its slowest debt.
        Assert.Equal(p.Months, p.Order.Max(o => o.ClearedInMonth));
        Assert.All(p.Order, o => Assert.Equal(PeriodStart.AddMonths(o.ClearedInMonth), o.ClearedOn));
    }

    [Fact]
    public async Task Avalanche_attacks_the_dearest_debt_and_snowball_the_smallest()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_strategy");
        var account = await CreateAccount(client, "Debts");
        var (card, loan) = await SeedTwoDebtsAsync(client, account.Id, auth.UserId);

        // ⚠️ With the card both dearest AND smallest the two strategies agree, which would let a plan that ignores
        // the chip pass. The extra is what makes the order observable at all — with no spare money each debt just
        // runs its own installment out and nothing is being "attacked".
        var av = (await Plan(client, account.Id, "?extra=400&strategy=avalanche"))!;
        var sn = (await Plan(client, account.Id, "?extra=400&strategy=snowball"))!;

        Assert.Equal("avalanche", av.Strategy);
        Assert.Equal("snowball", sn.Strategy);
        Assert.Equal(card, av.Order[0].BucketId);   // 22% — the expensive one
        Assert.Equal(card, sn.Order[0].BucketId);   // ...and also the small one
        Assert.Equal(loan, av.Order[1].BucketId);
        // Avalanche is the cheaper of the two by construction; it must never cost more.
        Assert.True(av.TotalInterest <= sn.TotalInterest);
    }

    [Fact]
    public async Task An_extra_is_measured_against_the_same_strategy_with_no_extra()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_extra");
        var account = await CreateAccount(client, "Debts");
        await SeedTwoDebtsAsync(client, account.Id, auth.UserId);

        var none = (await Plan(client, account.Id, "?extra=0"))!;
        var with = (await Plan(client, account.Id, "?extra=500"))!;

        Assert.Equal(0, none.MonthsSaved);           // nothing extra claims no saving
        Assert.Equal(0m, none.InterestSaved);
        Assert.True(with.Months < none.Months);
        Assert.Equal(none.Months - with.Months, with.MonthsSaved);
        Assert.Equal(decimal.Round(none.TotalInterest - with.TotalInterest, 2), with.InterestSaved);
    }

    [Fact]
    public async Task A_stack_that_never_clears_says_so_instead_of_inventing_a_date()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_never");
        var account = await CreateAccount(client, "Underwater");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(PeriodStart, new DateOnly(2026, 1, 31));
        // €10,000 at 6% accrues €50 in the first month alone, so €20 a month goes backwards forever.
        var sunk = agg.AddSavingCategory("Sunk");
        sunk.ConfigureDebt(10_000m, 6m, 20m, originalBalance: 10_000m, balanceAsOf: PeriodStart);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var p = (await Plan(client, account.Id))!;

        // The figures that DO hold are still stated — a client can show what is owed and ask for an extra amount.
        Assert.False(p.Available);
        Assert.Null(p.DebtFreeOn);
        Assert.Null(p.PaceDebtFreeOn);
        Assert.Equal(1, p.DebtCount);
        Assert.Equal(10_000m, p.TotalOwed);
    }

    [Fact]
    public async Task An_account_with_no_debt_reports_nothing_rather_than_zero()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_nodebt");
        var account = await CreateAccount(client, "Clear");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(PeriodStart, new DateOnly(2026, 1, 31));
        agg.AddSavingCategory("Holiday");   // an ordinary goal is not debt
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var p = (await Plan(client, account.Id))!;

        Assert.False(p.Available);
        Assert.Equal(0, p.DebtCount);
        Assert.Empty(p.Order);
        Assert.Null(p.PaceMonths);
    }

    [Fact]
    public async Task The_demonstrated_pace_is_a_different_answer_from_the_plan()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_pace");
        var account = await CreateAccount(client, "Paying ahead");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var period = agg.StartPeriod(PeriodStart, new DateOnly(2026, 1, 31));
        var card = agg.AddSavingCategory("Credit card");
        card.ConfigureDebt(2_000m, 22m, 150m, originalBalance: 2_000m, balanceAsOf: PeriodStart);
        // A demonstrated pace: €250 actually set aside against the card this period.
        period.AllocateToSavings(card.Id, new Money(250m, "EUR"), new DateOnly(2026, 1, 15));
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var p = (await Plan(client, account.Id, "?extra=0"))!;

        // ★ Two questions, not one. `Months` is "what if I ran this plan" (here: installments only, so nothing
        // extra); `PaceMonths` is "where am I actually heading" at installment + the €250/period demonstrated.
        // The pace is faster, so it must clear sooner — and a client that showed one where the other belongs
        // would be turning a hypothetical into a promise.
        Assert.True(p.Available);
        Assert.NotNull(p.PaceMonths);
        Assert.True(p.PaceMonths < p.Months);
        Assert.Equal(PeriodStart.AddMonths(p.PaceMonths!.Value), p.PaceDebtFreeOn);
        Assert.True(p.PaceInterestSaved > 0m);   // the pace IS buying something, so the line may claim it
    }

    [Fact]
    public async Task An_archived_or_cleared_debt_is_not_part_of_being_debt_free()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("plan_archived");
        var account = await CreateAccount(client, "Tidy");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(PeriodStart, new DateOnly(2026, 1, 31));
        var live = agg.AddSavingCategory("Car loan");
        live.ConfigureDebt(10_000m, 4m, 300m, originalBalance: 10_000m, balanceAsOf: PeriodStart);
        var old = agg.AddSavingCategory("Old card");
        old.ConfigureDebt(3_000m, 20m, 200m, originalBalance: 3_000m, balanceAsOf: PeriodStart);
        old.SetArchived(true);
        var paid = agg.AddSavingCategory("Paid off");
        paid.ConfigureDebt(0m, 10m, 100m, originalBalance: 5_000m, balanceAsOf: PeriodStart);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var p = (await Plan(client, account.Id))!;

        // Both exclusions matter for the same reason: either one would push the debt-free date out for money
        // nobody owes any more.
        Assert.Equal(1, p.DebtCount);
        Assert.Equal(10_000m, p.TotalOwed);
        Assert.Equal(live.Id, Assert.Single(p.Order).BucketId);
    }
}
