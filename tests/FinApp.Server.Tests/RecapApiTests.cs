using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The week-recap read — the last of R2.5's server-read rows.
///
/// <para>★ <b>These tests deliberately do not re-test the arithmetic.</b> Twenty domain tests in
/// <c>WeeklyRecapTests</c> already pin which week is covered, that carryover is not income, that a disbursement is
/// not negative saving, and that "left over" is measured against a typical week. Repeating them here would test
/// the same code twice and say nothing about the route.</para>
///
/// <para>What is only true at this boundary, and is what a thin client actually depends on: that ids arrive
/// already resolved to names and icons, that the caller's own date decides the week, and that every figure the
/// card prints is a field rather than something the client is expected to work out.</para>
/// </summary>
public class RecapApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RecapApiTests(FinAppServerFactory factory) => _factory = factory;

    // Wednesday. The last completed week is therefore Mon 10 Aug – Sun 16 Aug 2026.
    private static readonly DateOnly Today = new(2026, 8, 19);
    private const string Covered = "?today=2026-08-19";

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static Task<WeeklyRecapViewDto?> Recap(HttpClient client, Guid accountId, string query = Covered) =>
        client.GetFromJsonAsync<WeeklyRecapViewDto>($"/accounts/{accountId}/week-recap{query}");

    private static async Task SaveAsync(HttpClient client, Guid accountId, Account agg) =>
        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

    /// <summary>August, with spending in the covered week and in the one before it, one tag, and a round-up.</summary>
    private static Account AWeekOfSpending(Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(memberId, "Me");
        var food = agg.AddCategory("Food");
        food.SetIcon("utensils");
        var travel = agg.AddCategory("Travel");
        var tag = agg.AddTag("Holiday");
        tag.SetIcon("plane");
        var bank = agg.FundId("Bank");

        var p = agg.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        p.Deposit(memberId, new Money(2000m, "EUR"), fundId: bank);

        // The week before: 3–9 Aug.
        p.AddExpense(new Expense(food.Id, new Money(40m, "EUR"), new DateOnly(2026, 8, 5), memberId, bank));

        // The covered week: 10–16 Aug. Travel is the largest, and carries the note and the tag.
        p.AddExpense(new Expense(food.Id, new Money(25m, "EUR"), new DateOnly(2026, 8, 10), memberId, bank));
        p.AddExpense(new Expense(food.Id, new Money(15m, "EUR"), new DateOnly(2026, 8, 12), memberId, bank));
        var flight = new Expense(travel.Id, new Money(180m, "EUR"), new DateOnly(2026, 8, 14), memberId, bank,
            note: "Flights to Vienna");
        flight.SetTag(tag.Id);
        p.AddExpense(flight);

        // Outside the covered week entirely — it must not appear anywhere in the read.
        p.AddExpense(new Expense(food.Id, new Money(999m, "EUR"), new DateOnly(2026, 8, 20), memberId, bank));

        return agg;
    }

    [Fact]
    public async Task Ids_arrive_already_named_and_iconed_so_a_thin_client_needs_no_second_lookup()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_names");
        var account = await CreateAccount(client, "Recap");
        await SaveAsync(client, account.Id, AWeekOfSpending(auth.UserId));

        var r = (await Recap(client, account.Id))!;

        // The top category, the breakdown rows and the biggest expense each name themselves.
        Assert.Equal("Travel", r.TopCategoryName);
        Assert.Equal("Travel", r.Categories[0].Label);
        Assert.Equal("Food", r.Categories[1].Label);
        Assert.Equal("utensils", r.Categories[1].Icon);
        Assert.Equal("Travel", r.Biggest!.CategoryName);
        Assert.Equal("Flights to Vienna", r.Biggest.Note);
        Assert.Equal(new DateOnly(2026, 8, 14), r.Biggest.Date);

        // Tags too — the one surface where an id with no name would be invisible rather than merely ugly.
        Assert.Equal("Holiday", Assert.Single(r.Tags).Label);
        Assert.Equal("plane", r.Tags[0].Icon);
    }

    [Fact]
    public async Task The_callers_own_date_decides_which_week_is_covered()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_today");
        var account = await CreateAccount(client, "Recap");
        await SaveAsync(client, account.Id, AWeekOfSpending(auth.UserId));

        var wednesday = (await Recap(client, account.Id))!;
        Assert.Equal(new DateOnly(2026, 8, 10), wednesday.From);
        Assert.Equal(new DateOnly(2026, 8, 16), wednesday.To);
        Assert.Equal(220m, wednesday.Spent);          // 25 + 15 + 180, and not the 999 dated the 20th

        // A week later the window has moved on, and the covered week is now the one that was "next" above.
        var next = (await Recap(client, account.Id, "?today=2026-08-26"))!;
        Assert.Equal(new DateOnly(2026, 8, 17), next.From);
        Assert.Equal(999m, next.Spent);
        // ...and what was the headline a week ago is now the comparison.
        Assert.Equal(220m, next.PreviousSpent);
    }

    [Fact]
    public async Task Every_figure_the_card_prints_is_a_field_rather_than_a_sum_for_the_client_to_repeat()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_fields");
        var account = await CreateAccount(client, "Recap");
        await SaveAsync(client, account.Id, AWeekOfSpending(auth.UserId));

        var r = (await Recap(client, account.Id))!;

        Assert.Equal("EUR", r.Currency);
        Assert.Equal(220m, r.Spent);
        Assert.Equal(40m, r.PreviousSpent);
        Assert.Equal(180m, r.Change);                 // spent more than the week before: positive
        Assert.True(r.HasComparison);
        Assert.Equal(180m, r.TopCategorySpent);
        Assert.Equal(3, r.ExpenseCount);
        Assert.False(r.IsEmpty);

        // ⚠️ The income the client prints is NOT the money that literally arrived. One €2,000 deposit in a month
        // is smoothed to a typical week, and `Net` is measured against that — otherwise three weeks in four report
        // a loss the user did not have. Both the figure and the flag that changes its LABEL come down the wire.
        Assert.True(r.IncomeIsTypical);
        Assert.Equal(0m, r.Income);                   // nothing landed inside 10–16 Aug
        Assert.Equal(461.54m, r.EffectiveIncome);     // 2000 / (52/12), rounded
        Assert.Equal(r.EffectiveIncome - r.Spent, r.Net);
    }

    [Fact]
    public async Task An_untagged_account_reports_no_tags_rather_than_a_placeholder_row()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_untagged");
        var account = await CreateAccount(client, "Recap");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        var food = agg.AddCategory("Food").Id;
        var p = agg.StartPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        p.AddExpense(new Expense(food, new Money(30m, "EUR"), new DateOnly(2026, 8, 11), auth.UserId, agg.FundId("Bank")));
        await SaveAsync(client, account.Id, agg);

        var r = (await Recap(client, account.Id))!;

        Assert.Empty(r.Tags);
        Assert.Single(r.Categories);
    }

    /// <summary>⚠️ There are TWO ways to be empty and they do not answer alike, so both are pinned here. A brand
    /// new account has no snapshot at all and short-circuits before the map runs, which is why its currency comes
    /// back blank; an account that has been saved but has no periods goes through the map and keeps its currency.
    /// <b><see cref="WeeklyRecapViewDto.IsEmpty"/> is the client's only gate</b> — it is true on both paths, and a
    /// client reading currency instead would draw a card for one of them.</summary>
    [Fact]
    public async Task Both_kinds_of_empty_report_IsEmpty_rather_than_failing_or_reporting_zeroes()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_empty");

        var fresh = await CreateAccount(client, "Fresh");
        var never = (await Recap(client, fresh.Id))!;
        Assert.True(never.IsEmpty);
        Assert.Empty(never.Categories);
        Assert.Null(never.Biggest);
        Assert.Equal("", never.Currency);        // no snapshot to read a currency from

        var saved = await CreateAccount(client, "Saved");
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        agg.AddMember(auth.UserId, "Me");
        await SaveAsync(client, saved.Id, agg);

        var noPeriods = (await Recap(client, saved.Id))!;
        Assert.True(noPeriods.IsEmpty);
        Assert.Equal("EUR", noPeriods.Currency);
    }

    [Fact]
    public async Task A_quiet_week_after_a_busy_one_is_not_empty_because_the_comparison_is_the_news()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("wr_quiet");
        var account = await CreateAccount(client, "Recap");
        await SaveAsync(client, account.Id, AWeekOfSpending(auth.UserId));

        // Covered week 17–23 Aug holds only the 999; the week before it is the busy one.
        var r = (await Recap(client, account.Id, "?today=2026-08-26"))!;

        Assert.False(r.IsEmpty);
        Assert.True(r.HasComparison);
        Assert.Equal(779m, r.Change);
    }

    [Fact]
    public async Task Stranger_cannot_read_the_recap()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("wr_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("wr_intruder");
        var account = await CreateAccount(owner, "Private");
        await SaveAsync(owner, account.Id, AWeekOfSpending(auth.UserId));

        var resp = await stranger.GetAsync($"/accounts/{account.Id}/week-recap{Covered}");

        Assert.False(resp.IsSuccessStatusCode);
    }
}
