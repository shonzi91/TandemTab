using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Breakdown read.
/// <para>
/// ⚠️ These tests exist because this chart's shape was argued out over three attempts and one revert, and the
/// rules that make it honest all look like details until they are gone: the ring is spending, "Spent" equals the
/// slices, "Set aside" cannot go negative, and a transfer is ranked in rather than appended. Each one below broke
/// something visible on the web when it was absent.
/// </para>
/// </summary>
public class BreakdownApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public BreakdownApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 1, 31);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task SaveAsync(HttpClient client, Guid accountId, Account agg) =>
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

    private static Task<BreakdownViewDto?> Breakdown(HttpClient client, Guid accountId, string? groupBy = null) =>
        client.GetFromJsonAsync<BreakdownViewDto>(
            $"/accounts/{accountId}/breakdown" + (groupBy is null ? "" : $"?groupBy={groupBy}"));

    /// <summary>An account with an open Jan-2026 period, €5,000 deposited, and Food/Travel categories.</summary>
    private static (Account Agg, Guid Food, Guid Travel, Guid Fund) Seed(Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var food = agg.AddCategory("Food").Id;
        var travel = agg.AddCategory("Travel").Id;
        var fund = agg.FundId("Bank");
        var period = agg.StartPeriod(From, To);
        period.Deposit(memberId, new Money(5_000m, "EUR"), fundId: fund);
        return (agg, food, travel, fund);
    }

    [Fact]
    public async Task Spent_equals_the_sum_of_the_slices()
    {
        // ★ The fix that made this chart trustworthy. The two used to differ by the savings slice, silently: the
        // ring counted money merely earmarked, the figure beside it did not, and nothing said which question
        // either was answering.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_total");
        var account = await CreateAccount(client, "Break");
        var (agg, food, travel, fund) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(100m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        period.AddExpense(new Expense(travel, new Money(40m, "EUR"), new DateOnly(2026, 1, 8), auth.UserId, fund));
        await SaveAsync(client, account.Id, agg);

        var b = (await Breakdown(client, account.Id))!;

        Assert.Equal(140m, b.Spent);
        Assert.Equal(b.Spent, b.Slices.Sum(s => s.Amount));
        Assert.Equal("category", b.GroupBy);
        Assert.Equal(From, b.From);
        Assert.Equal(To, b.To);
    }

    [Fact]
    public async Task Money_set_aside_is_not_a_slice_but_is_reported_beside_the_ring()
    {
        // ★★ THE RING IS SPENDING. Savings never left the account, so they are not part of a composition of what
        // did — but nothing is hidden: the figure is stated next to it.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_saving");
        var account = await CreateAccount(client, "Break");
        var (agg, food, _, fund) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(100m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        var bucket = agg.AddSavingCategory("Holiday");
        period.AllocateToSavings(bucket.Id, new Money(300m, "EUR"), new DateOnly(2026, 1, 10));
        await SaveAsync(client, account.Id, agg);

        var b = (await Breakdown(client, account.Id))!;

        Assert.Equal(100m, b.Spent);                                   // the saving is NOT spending
        Assert.Equal(100m, b.Slices.Sum(s => s.Amount));               // …and NOT a slice
        Assert.Equal(300m, b.SetAside);                                // …but it is on screen
        Assert.DoesNotContain(b.Slices, s => s.Label == "Holiday");
    }

    [Fact]
    public async Task Set_aside_never_goes_negative()
    {
        // ⚠️ A signed version of this figure printed "−€3,320" beside a hero reading €450 on the same screen.
        // Nothing is un-saved when an earlier month's earmark reaches the thing it was for.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_negative");
        var account = await CreateAccount(client, "Break");
        var (agg, food, _, _) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        var bucket = agg.AddSavingCategory("Car loan");
        period.AllocateToSavings(bucket.Id, new Money(200m, "EUR"), new DateOnly(2026, 1, 3));
        // Releasing back into a budget IS the one drawdown that really un-saves — and releasing more than was put
        // in this period is what drove the figure negative on the web.
        period.ConvertSavingToBudget(bucket.Id, food, new Money(900m, "EUR"), new DateOnly(2026, 1, 20));
        await SaveAsync(client, account.Id, agg);

        var b = (await Breakdown(client, account.Id))!;
        Assert.True(b.SetAside >= 0m);
    }

    [Fact]
    public async Task A_transfer_out_is_ranked_in_by_size_not_appended()
    {
        // ★ Appending sorted a transfer last however large it was, so for anyone moving real money between
        // accounts the single biggest outflow of the period sat at the bottom of a list ordered by size.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_transfer");
        var account = await CreateAccount(client, "Break");
        var other = await CreateAccount(client, "Other");
        var (agg, food, _, fund) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(30m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        period.TransferOut(fund, new Money(900m, "EUR"), new DateOnly(2026, 1, 9), other.Id, null);
        await SaveAsync(client, account.Id, agg);

        var b = (await Breakdown(client, account.Id))!;

        Assert.Equal(930m, b.Spent);                        // a transfer out IS money leaving
        Assert.Equal("Transfers out", b.Slices[0].Label);   // …and it is the biggest, so it leads
        Assert.Equal(900m, b.Slices[0].Amount);
        Assert.Equal(b.Spent, b.Slices.Sum(s => s.Amount));
    }

    [Fact]
    public async Task A_goal_payout_is_named_rather_than_left_as_a_total()
    {
        // A payout is a real outflow and gets its own figure — but never a slice: a €12,000 prepayment beside €30
        // of groceries is not a composition, and it squeezed the only actionable slice to 2.7% when it was tried.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_payout");
        var account = await CreateAccount(client, "Break");
        var (agg, food, _, fund) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(30m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        var bucket = agg.AddSavingCategory("Car loan");
        period.AllocateToSavings(bucket.Id, new Money(1_000m, "EUR"), new DateOnly(2026, 1, 6));
        period.DisburseSaving(bucket.Id, fund, new Money(1_000m, "EUR"), new DateOnly(2026, 1, 20), null);
        await SaveAsync(client, account.Id, agg);

        var b = (await Breakdown(client, account.Id))!;

        Assert.Equal(30m, b.Spent);                                      // the payout is not spending
        Assert.Equal(30m, b.Slices.Sum(s => s.Amount));                  // …nor a slice
        Assert.Equal(1_000m, b.PaidToGoals);                             // …but it is stated
        Assert.Contains(b.Payouts, p => p.Name == "Car loan" && p.Amount == 1_000m);   // …and named
    }

    [Fact]
    public async Task An_expense_labelled_with_a_tag_still_files_under_its_category()
    {
        // ⚠️ The sub-category rollup in BreakdownMap is for LEGACY data only, and this test says why rather than
        // asserting a state the app can no longer reach: `Account.AddCategory` takes a `parentId` and silently
        // ignores it, because the sub-category tree was collapsed into tags (see `Account.CollapseSubCategories`).
        // Tags are the axis sub-categories used to be, so the modern shape of "Takeaway inside Food" is a tag.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_subcat");
        var account = await CreateAccount(client, "Break");
        var (agg, food, _, fund) = Seed(auth.UserId);
        var tag = agg.AddTag("Takeaway");
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(20m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        var labelled = new Expense(food, new Money(15m, "EUR"), new DateOnly(2026, 1, 6), auth.UserId, fund);
        labelled.SetTag(tag.Id);
        period.AddExpense(labelled);
        await SaveAsync(client, account.Id, agg);

        // By category: one wedge. The label does not split the budget.
        var byCategory = (await Breakdown(client, account.Id))!;
        var slice = Assert.Single(byCategory.Slices);
        Assert.Equal("Food", slice.Label);
        Assert.Equal(35m, slice.Amount);

        // By tag: two wedges, and the untagged spend is "Untagged" rather than vanishing.
        var byTag = (await Breakdown(client, account.Id, "tag"))!;
        Assert.Equal(2, byTag.Slices.Count);
        Assert.Contains(byTag.Slices, s => s.Label == "Takeaway" && s.Amount == 15m);
        Assert.Contains(byTag.Slices, s => s.Label == "Untagged" && s.Amount == 20m);
        Assert.Equal(byCategory.Spent, byTag.Spent);   // the grouping never changes what left the account
    }

    [Fact]
    public async Task Grouping_by_fund_regroups_the_same_total()
    {
        // The grouping changes which wedges appear; it must never change how much left the account.
        var (client, auth) = await _factory.RegisterAndAuthAsync("bd_byfund");
        var account = await CreateAccount(client, "Break");
        var (agg, food, travel, fund) = Seed(auth.UserId);
        var period = agg.CurrentPeriod!;
        period.AddExpense(new Expense(food, new Money(20m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, fund));
        period.AddExpense(new Expense(travel, new Money(15m, "EUR"), new DateOnly(2026, 1, 6), auth.UserId, fund));
        await SaveAsync(client, account.Id, agg);

        var byCategory = (await Breakdown(client, account.Id))!;
        var byFund = (await Breakdown(client, account.Id, "fund"))!;

        Assert.Equal(byCategory.Spent, byFund.Spent);
        Assert.Equal(2, byCategory.Slices.Count);              // Food + Travel
        Assert.Single(byFund.Slices);                          // both came out of one wallet
        Assert.Equal("fund", byFund.GroupBy);
    }

    [Fact]
    public async Task Stranger_cannot_read_a_breakdown()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("bd_owner");
        var account = await CreateAccount(owner, "Break");
        var (agg, _, _, _) = Seed(auth.UserId);
        await SaveAsync(owner, account.Id, agg);

        var (stranger, _) = await _factory.RegisterAndAuthAsync("bd_stranger");
        var resp = await stranger.GetAsync($"/accounts/{account.Id}/breakdown");
        Assert.False(resp.IsSuccessStatusCode);
    }
}
