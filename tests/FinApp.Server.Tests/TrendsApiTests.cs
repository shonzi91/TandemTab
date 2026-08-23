using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The Trends read — one row per period, the month-by-month view no thin contract carried.
///
/// <para>★ What is worth testing here is not "does it add up" but <b>which things it adds up</b>. Every rule below
/// is one the web argued out and the two clients must not disagree about: an out-transfer is money that left, a
/// drawdown is not negative saving, and a debt disbursement is its own fact rather than spending. Each of those
/// getting silently different on the phone is exactly how a figure comes to contradict the tile above it.</para>
/// </summary>
public class TrendsApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public TrendsApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static Task<TrendsViewDto?> Trends(HttpClient client, Guid accountId, string query = "") =>
        client.GetFromJsonAsync<TrendsViewDto>($"/accounts/{accountId}/trends{query}");

    private static async Task SaveAsync(HttpClient client, Guid accountId, Account agg) =>
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

    /// <summary>Three consecutive months, each with a deposit and one expense, so the rows are distinguishable.</summary>
    private static Account ThreeMonths(Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        for (var m = 1; m <= 3; m++)
        {
            var p = agg.StartPeriod(new DateOnly(2026, m, 1), new DateOnly(2026, m, 28));
            p.Deposit(memberId, new Money(1000m * m, "EUR"), fundId: bank);
            p.AddExpense(new Expense(food, new Money(100m * m, "EUR"), new DateOnly(2026, m, 10), memberId, bank));
        }
        return agg;
    }

    [Fact]
    public async Task All_time_returns_one_row_per_period_oldest_first()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_all");
        var account = await CreateAccount(client, "Trends");
        await SaveAsync(client, account.Id, ThreeMonths(auth.UserId));

        var t = (await Trends(client, account.Id))!;

        Assert.Equal("EUR", t.Currency);
        Assert.Null(t.From);
        Assert.Null(t.To);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), t.Rows[0].From);
        Assert.Equal(new DateOnly(2026, 3, 1), t.Rows[2].From);
        Assert.Equal(1000m, t.Rows[0].Income);
        Assert.Equal(100m, t.Rows[0].Spent);
        Assert.Equal(900m, t.Rows[0].Net);        // what the month actually kept
        Assert.Equal(3000m, t.Rows[2].Income);
    }

    [Fact]
    public async Task A_window_selects_whole_periods_that_overlap_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_window");
        var account = await CreateAccount(client, "Trends");
        await SaveAsync(client, account.Id, ThreeMonths(auth.UserId));

        // ⚠️ A window that starts mid-February still returns February WHOLE. Cutting a period at the window's
        // edge would print a half-month beside full ones and invite exactly the comparison the chart is for.
        var t = (await Trends(client, account.Id, "?from=2026-02-14&to=2026-03-20"))!;

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), t.Rows[0].From);
        Assert.Equal(2000m, t.Rows[0].Income);   // February entire, not the part inside the window
        Assert.Equal(new DateOnly(2026, 2, 14), t.From);
    }

    [Fact]
    public async Task Spent_counts_money_sent_to_another_account()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_xfer");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var food = agg.AddCategory("Food").Id;
        var bank = agg.FundId("Bank");
        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.SetInitialBalance(bank, new Money(2000m, "EUR"));
        p.AddExpense(new Expense(food, new Money(100m, "EUR"), new DateOnly(2026, 1, 10), auth.UserId, bank));
        await SaveAsync(client, source.Id, agg);

        var destAgg = new Account("Seed", "EUR");
        destAgg.AddDefaultFunds();
        destAgg.AddMember(auth.UserId, "Me");
        destAgg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await SaveAsync(client, dest.Id, destAgg);

        (await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, bank, 400m, Date: new DateOnly(2026, 1, 12))))
            .EnsureSuccessStatusCode();

        var t = (await Trends(client, source.Id))!;

        // ★ Both are money that left, which is what makes this agree with the Home "Spent" tile and the
        // Breakdown. A Trends that counted only expenses would disagree with both, on the same screen.
        Assert.Equal(500m, t.Rows[0].Spent);
        Assert.Equal(-500m, t.Rows[0].Net);   // nothing came in, so the month is negative — and says so
    }

    [Fact]
    public async Task Saved_is_floored_at_zero_and_a_debt_payout_is_its_own_column()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_saved");
        var account = await CreateAccount(client, "Trends");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var bank = agg.FundId("Bank");
        var loan = agg.AddSavingCategory("Car loan");
        loan.ConfigureDebt(5_000m, 5m, 200m, originalBalance: 5_000m, balanceAsOf: new DateOnly(2026, 1, 1));

        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Deposit(auth.UserId, new Money(1000m, "EUR"), fundId: bank);
        p.AllocateToSavings(loan.Id, new Money(300m, "EUR"), new DateOnly(2026, 1, 5));
        // The bucket deploys MORE than went in this month, so the net is negative.
        p.DisburseSaving(loan.Id, bank, new Money(800m, "EUR"), new DateOnly(2026, 1, 20));
        await SaveAsync(client, account.Id, agg);

        var t = (await Trends(client, account.Id))!;

        // ★ Not −500. A month whose bucket paid out is not a month of negative saving; the payout is a different
        // fact and DebtPaid is its slot. A signed version of this once printed "−€3,320" beside a hero reading
        // €450 on the same screen.
        Assert.Equal(0m, t.Rows[0].Saved);
        Assert.Equal(800m, t.Rows[0].DebtPaid);
    }

    [Fact]
    public async Task A_goal_payout_is_not_counted_as_debt_repaid()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_goalout");
        var account = await CreateAccount(client, "Trends");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var holiday = agg.AddSavingCategory("Holiday");   // an ordinary goal, not debt
        var bank = agg.FundId("Bank");
        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.AllocateToSavings(holiday.Id, new Money(600m, "EUR"), new DateOnly(2026, 1, 5));
        p.DisburseSaving(holiday.Id, bank, new Money(600m, "EUR"), new DateOnly(2026, 1, 20));
        await SaveAsync(client, account.Id, agg);

        var t = (await Trends(client, account.Id))!;

        // Deploying a holiday fund at a holiday is spending by another route; deploying one at a loan is the
        // balance falling because the debt did. Only the second is this column.
        Assert.Equal(0m, t.Rows[0].DebtPaid);
    }

    [Fact]
    public async Task Focusing_a_category_narrows_the_second_series_to_it_alone()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_focus_cat");
        var account = await CreateAccount(client, "Trends");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var bank = agg.FundId("Bank");
        var food = agg.AddCategory("Food");
        var transport = agg.AddCategory("Transport");
        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.AddExpense(new Expense(food.Id, new Money(60m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, bank));
        p.AddExpense(new Expense(food.Id, new Money(25m, "EUR"), new DateOnly(2026, 1, 6), auth.UserId, bank));
        p.AddExpense(new Expense(transport.Id, new Money(40m, "EUR"), new DateOnly(2026, 1, 7), auth.UserId, bank));
        await SaveAsync(client, account.Id, agg);

        var t = (await Trends(client, account.Id, $"?focus=category&focusId={food.Id}"))!;

        Assert.Equal("category", t.FocusKind);
        Assert.Equal("Food", t.FocusName);
        Assert.Equal(85m, t.Rows[0].Focus);    // Food only, not Transport
        Assert.Equal(125m, t.Rows[0].Spent);   // the un-narrowed total is untouched by the focus
        // ⚠️ The map's parent roll-up (RootCategoryId) cannot be exercised here, and that is not an oversight:
        // FlattenCategoryTree runs inside Deserialize, so a sub-category becomes a Tag the first time the account
        // is loaded and no nesting survives to roll up. It mirrors the web's TrendFocusValue line for line — which
        // is a no-op there for the same reason — and keeping the mirror is cheaper than explaining an asymmetry.
    }

    [Fact]
    public async Task A_categorised_transfer_out_counts_toward_the_focused_category()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_focus_xfer");
        var source = await CreateAccount(client, "Source");
        var dest = await CreateAccount(client, "Dest");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var household = agg.AddCategory("Household");
        var bank = agg.FundId("Bank");
        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.SetInitialBalance(bank, new Money(2000m, "EUR"));
        p.AddExpense(new Expense(household.Id, new Money(50m, "EUR"), new DateOnly(2026, 1, 5), auth.UserId, bank));
        await SaveAsync(client, source.Id, agg);

        var destAgg = new Account("Seed", "EUR");
        destAgg.AddDefaultFunds();
        destAgg.AddMember(auth.UserId, "Me");
        destAgg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await SaveAsync(client, dest.Id, destAgg);

        (await client.PostAsJsonAsync($"/accounts/{source.Id}/transfers-out",
            new TransferToAccountRequest(dest.Id, bank, 400m, Date: new DateOnly(2026, 1, 12),
                CategoryId: household.Id))).EnsureSuccessStatusCode();

        var t = (await Trends(client, source.Id, $"?focus=category&focusId={household.Id}"))!;

        // ★ Exactly as the budget rings and the By-budgets header count it since O4. If it did not, the trend for
        // "Household" would quietly disagree with the Household budget on the screen next door, and the reader
        // would have no way to tell which one was lying.
        Assert.Equal(450m, t.Rows[0].Focus);
    }

    [Fact]
    public async Task Focusing_a_bucket_reports_its_net_for_the_month_floored_at_zero()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_focus_bucket");
        var account = await CreateAccount(client, "Trends");

        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var holiday = agg.AddSavingCategory("Holiday");
        var other = agg.AddSavingCategory("Rainy day");
        var bank = agg.FundId("Bank");
        var p = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.AllocateToSavings(holiday.Id, new Money(300m, "EUR"), new DateOnly(2026, 1, 5));
        p.DisburseSaving(holiday.Id, bank, new Money(100m, "EUR"), new DateOnly(2026, 1, 20));
        p.AllocateToSavings(other.Id, new Money(999m, "EUR"), new DateOnly(2026, 1, 6));
        await SaveAsync(client, account.Id, agg);

        var t = (await Trends(client, account.Id, $"?focus=bucket&focusId={holiday.Id}"))!;

        Assert.Equal("bucket", t.FocusKind);
        Assert.Equal("Holiday", t.FocusName);
        Assert.Equal(200m, t.Rows[0].Focus);   // this bucket's net — the other bucket is not in it
    }

    [Fact]
    public async Task A_focus_naming_something_that_no_longer_exists_falls_back_to_the_whole_account()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("tr_focus_ghost");
        var account = await CreateAccount(client, "Trends");
        await SaveAsync(client, account.Id, ThreeMonths(auth.UserId));

        // A stale id from a deleted category narrows to nothing. A chart of zeroes under a name is worse than
        // the un-narrowed one, so the read declines the focus rather than honouring it emptily.
        var t = (await Trends(client, account.Id, $"?focus=category&focusId={Guid.NewGuid()}"))!;

        Assert.Null(t.FocusKind);
        Assert.Null(t.FocusName);
        Assert.Equal(Guid.Empty, t.FocusId);
        Assert.Equal(3, t.Rows.Count);
    }

    [Fact]
    public async Task An_account_with_no_periods_returns_an_empty_read_rather_than_failing()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("tr_empty");
        var account = await CreateAccount(client, "Fresh");

        var t = (await Trends(client, account.Id))!;

        Assert.Empty(t.Rows);
    }

    [Fact]
    public async Task Stranger_cannot_read_the_trends()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("tr_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("tr_intruder");
        var account = await CreateAccount(owner, "Private");
        await SaveAsync(owner, account.Id, ThreeMonths(auth.UserId));

        var resp = await stranger.GetAsync($"/accounts/{account.Id}/trends");

        Assert.False(resp.IsSuccessStatusCode);
    }
}
