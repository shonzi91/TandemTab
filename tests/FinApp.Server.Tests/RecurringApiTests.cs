using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Recurring;

namespace FinApp.Server.Tests;

/// <summary>
/// Recurring items — CRUD + pause/resume + the due-item handlers (confirm posts a real transaction and tunes a
/// "typical" estimate; skip marks handled without posting). Mirrors the client's recurring methods; confirm/skip
/// go through the shared <c>Period.PostRecurring</c>. CRUD verified via snapshot; confirm/skip via /overview.
/// </summary>
public class RecurringApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public RecurringApiTests(FinAppServerFactory factory) => _factory = factory;

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    /// <summary>Seed: funds + a "Rent" spend category + a "Salary" contribution category + an open Jan-2026 period
    /// (no deposit). Returns (rent, salary, bankFund).</summary>
    private static async Task<(Guid Rent, Guid Salary, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rent = agg.AddCategory("Rent").Id;
        var salary = agg.AddContributionCategory("Salary").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (rent, salary, fund);
    }

    private static async Task<Account> LoadAsync(HttpClient client, Guid accountId) =>
        AccountSnapshotSerializer.Deserialize((await client.GetFromJsonAsync<AccountSnapshot>($"/accounts/{accountId}/snapshot"))!.Payload);

    private static Task<AccountOverviewDto?> Overview(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<AccountOverviewDto>($"/accounts/{accountId}/overview");

    private static async Task<Guid> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MutationResultDto>())!.EntityId!.Value;
    }

    [Fact]
    public async Task Create_recurring_expense_stores_its_fields()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_create");
        var account = await CreateAccount(client, "Data");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund, Icon: "home", AutoPost: true)));

        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal("Rent", item.Name);
        Assert.Equal(RecurringKind.Expense, item.Kind);
        Assert.Equal(RecurringAmountMode.Fixed, item.AmountMode);
        Assert.Equal(500m, item.ExpectedAmount);
        Assert.Equal(15, item.DayOfMonth);
        Assert.Equal(rent, item.CategoryId);
        Assert.True(item.AutoPost);
        Assert.NotNull(item.CreatedOn);
    }

    [Fact]
    public async Task Update_then_pause_a_recurring_item()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_update");
        var account = await CreateAccount(client, "Upd");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}",
            new UpdateRecurringRequest("Rent+", "typical", 550m, 20, rent, fund))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/active", new SetActiveRequest(false))).EnsureSuccessStatusCode();

        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal("Rent+", item.Name);
        Assert.Equal(RecurringAmountMode.Typical, item.AmountMode);
        Assert.Equal(550m, item.ExpectedAmount);
        Assert.Equal(20, item.DayOfMonth);
        Assert.False(item.Active);
    }

    [Fact]
    public async Task Delete_a_recurring_item()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_del");
        var account = await CreateAccount(client, "Del");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.DeleteAsync($"/accounts/{account.Id}/recurring/{recId}")).EnsureSuccessStatusCode();
        Assert.Null((await LoadAsync(client, account.Id)).FindRecurring(recId));
    }

    [Fact]
    public async Task Confirm_posts_an_expense_and_tunes_a_typical_estimate()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_confirm");
        var account = await CreateAccount(client, "Confirm");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Utilities", "expense", "typical", 500m, 15, rent, fund)));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(520m))).EnsureSuccessStatusCode();

        Assert.Equal(520m, (await Overview(client, account.Id))!.Spent);   // the real amount posted
        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal(510m, item.ExpectedAmount);                          // typical nudged halfway: (500+520)/2
        Assert.Equal(new DateOnly(2026, 1, 1), item.LastHandledPeriodFrom); // marked handled for this period
    }

    [Fact]
    public async Task Confirm_income_posts_a_deposit()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_income");
        var account = await CreateAccount(client, "Income");
        var (_, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Salary", "income", "fixed", 2000m, 1, salary, fund)));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(2000m))).EnsureSuccessStatusCode();

        Assert.Equal(2000m, (await Overview(client, account.Id))!.Contributed);
    }

    [Fact]
    public async Task Skip_marks_handled_without_posting()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_skip");
        var account = await CreateAccount(client, "Skip");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PostAsync($"/accounts/{account.Id}/recurring/{recId}/skip", content: null)).EnsureSuccessStatusCode();

        Assert.Equal(0m, (await Overview(client, account.Id))!.Spent);   // nothing posted
        var skipped = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal(new DateOnly(2026, 1, 1), skipped.LastHandledPeriodFrom);
        Assert.True(skipped.LastHandledWasSkip);   // recorded as a skip, so it stays undoable
    }

    [Fact]
    public async Task Unskip_makes_the_item_due_again()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_unskip");
        var account = await CreateAccount(client, "Unskip");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PostAsync($"/accounts/{account.Id}/recurring/{recId}/skip", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/accounts/{account.Id}/recurring/{recId}/unskip", content: null)).EnsureSuccessStatusCode();

        var back = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Null(back.LastHandledPeriodFrom);
        Assert.Equal(0m, (await Overview(client, account.Id))!.Spent);   // undoing a skip still posts nothing
    }

    [Fact]
    public async Task Unskip_refuses_on_a_confirmed_item()
    {
        // The bill's expense is already booked; re-arming it would invite a second payment. The refusal lives in
        // the domain, so it holds however the request arrives.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_unskip_posted");
        var account = await CreateAccount(client, "UnskipPosted");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund)));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(500m))).EnsureSuccessStatusCode();

        var resp = await client.PostAsync($"/accounts/{account.Id}/recurring/{recId}/unskip", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(500m, (await Overview(client, account.Id))!.Spent);   // and the expense is untouched
    }

    [Fact]
    public async Task A_bill_linked_to_a_loan_takes_the_loans_installment_day()
    {
        // The loan states the contractual day, so the bill moves onto it — the two could previously disagree, with
        // the debt row reading "due on the 30th" over a bill set to day 15.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_loanday");
        var account = await CreateAccount(client, "LoanDay");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m, DebtInstallmentDay: 28)));

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car payment", "expense", "fixed", 400m, 15, rent, fund, LinkedDebtBucketId: loanId)));

        Assert.Equal(28, (await LoadAsync(client, account.Id)).FindRecurring(recId)!.DayOfMonth);
    }

    [Fact]
    public async Task A_loan_with_no_day_of_its_own_takes_the_bills()
    {
        // The other direction: nothing contractual is stated, so the bill's day fills the gap rather than the two
        // staying independently unknown.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_billday");
        var account = await CreateAccount(client, "BillDay");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m)));

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car payment", "expense", "fixed", 400m, 9, rent, fund, LinkedDebtBucketId: loanId)));

        var loaded = await LoadAsync(client, account.Id);
        Assert.Equal(9, loaded.FindRecurring(recId)!.DayOfMonth);
        Assert.Equal(9, loaded.FindSavingCategory(loanId)!.DebtInstallmentDay);
    }

    [Fact]
    public async Task Moving_the_loans_day_moves_the_bill_that_services_it()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_moveday");
        var account = await CreateAccount(client, "MoveDay");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m, DebtInstallmentDay: 10)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car payment", "expense", "fixed", 400m, 10, rent, fund, LinkedDebtBucketId: loanId)));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{loanId}",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m, DebtInstallmentDay: 22))).EnsureSuccessStatusCode();

        Assert.Equal(22, (await LoadAsync(client, account.Id)).FindRecurring(recId)!.DayOfMonth);
    }

    [Fact]
    public async Task Linking_a_bill_switches_the_loan_onto_logged_payments()
    {
        // Linking says "I pay this loan through this app" — which is exactly what the setting was asking for.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_paydriven");
        var account = await CreateAccount(client, "PayDriven");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m)));
        Assert.False((await LoadAsync(client, account.Id)).FindSavingCategory(loanId)!.DebtPaymentDriven);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car payment", "expense", "fixed", 400m, 10, rent, fund, LinkedDebtBucketId: loanId)));

        var loan = (await LoadAsync(client, account.Id)).FindSavingCategory(loanId)!;
        Assert.True(loan.DebtPaymentDriven);
        Assert.Equal(8000m, loan.DebtBalance);   // the mode changed; what's owed did not
    }

    [Fact]
    public async Task Re_saving_a_bill_does_not_undo_a_deliberate_switch_back_to_the_schedule()
    {
        // ★ The default fires on the transition INTO a link, never on every save. A user whose bill is only a
        // reminder for a payment leaving an account this app can't see must be able to keep schedule-driven.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_paydriven_keep");
        var account = await CreateAccount(client, "KeepSchedule");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m, DebtInstallment: 400m)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car payment", "expense", "fixed", 400m, 10, rent, fund, LinkedDebtBucketId: loanId)));

        // The user deliberately puts it back on its own schedule...
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/savings/buckets/{loanId}",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 8000m, DebtRate: 6m,
                DebtInstallment: 400m, DebtPaymentDriven: false))).EnsureSuccessStatusCode();

        // ...then edits the bill for an unrelated reason.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}",
            new UpdateRecurringRequest("Car payment (renamed)", "fixed", 400m, 10, rent, fund, null, false, loanId)))
            .EnsureSuccessStatusCode();

        Assert.False((await LoadAsync(client, account.Id)).FindSavingCategory(loanId)!.DebtPaymentDriven);
    }

    // --- C: the part of a debt-linked bill above the loan's contractual installment ---------------------

    [Fact]
    public async Task An_excess_category_round_trips_through_the_view_with_its_name_resolved()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_excess");
        var account = await CreateAccount(client, "Excess");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var insurance = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Insurance")));

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 700m, 10, rent, fund,
                LinkedDebtBucketId: loanId, ExcessCategoryId: insurance, ExcessLabel: "Health + property")));

        var row = (await client.GetFromJsonAsync<RecurringViewDto>($"/accounts/{account.Id}/recurring"))!
            .Items.Single(i => i.Id == recId);
        Assert.Equal(insurance, row.ExcessCategoryId);
        Assert.Equal("Insurance", row.ExcessCategoryName);   // resolved server-side, like LinkedDebtName
        Assert.Equal("Health + property", row.ExcessLabel);
    }

    [Fact]
    public async Task An_edit_that_omits_the_excess_category_leaves_it_alone()
    {
        // ★ The whole reason UpdateRecurringRequest.ExcessCategoryId is NOT authoritative. Android writes this
        // route, and an older build that has never heard of the field sends null on every bill edit — which under
        // the authoritative rule would silently wipe the configuration and put €100 back onto the loan next month.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_excess_keep");
        var account = await CreateAccount(client, "ExcessKeep");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var insurance = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Insurance")));

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 700m, 10, rent, fund,
                LinkedDebtBucketId: loanId, ExcessCategoryId: insurance, ExcessLabel: "Health + property")));

        // An old client's edit: every field it knows about, nothing it doesn't.
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}",
            new UpdateRecurringRequest("Car loan (renamed)", "fixed", 700m, 10, rent, fund, null, false, loanId)))
            .EnsureSuccessStatusCode();

        var item = (await LoadAsync(client, account.Id)).FindRecurring(recId)!;
        Assert.Equal(insurance, item.ExcessCategoryId);
        Assert.Equal("Health + property", item.ExcessLabel);
    }

    [Fact]
    public async Task An_edit_sending_the_empty_guid_clears_the_excess_category()
    {
        // The other half of the three-state field: the web form says Guid.Empty when the user picks
        // "extra payment onto the loan", and that must actually clear it.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_excess_clear");
        var account = await CreateAccount(client, "ExcessClear");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var insurance = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Insurance")));

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 700m, 10, rent, fund,
                LinkedDebtBucketId: loanId, ExcessCategoryId: insurance)));

        (await client.PutAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}",
            new UpdateRecurringRequest("Car loan", "fixed", 700m, 10, rent, fund, null, false, loanId, Guid.Empty)))
            .EnsureSuccessStatusCode();

        Assert.Null((await LoadAsync(client, account.Id)).FindRecurring(recId)!.ExcessCategoryId);
    }

    [Fact]
    public async Task An_excess_category_that_doesnt_exist_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_excess_badcat");
        var account = await CreateAccount(client, "ExcessBadCat");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));

        var response = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 700m, 10, rent, fund,
                LinkedDebtBucketId: loanId, ExcessCategoryId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Confirming_a_bill_over_the_installment_posts_three_rows()
    {
        // End to end, through the server's own confirm route: €600 services the loan (interest + principal) and
        // the €100 that is really insurance stands as its own line under its own category.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_excess_confirm");
        var account = await CreateAccount(client, "ExcessConfirm");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);
        var insurance = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/categories",
            new CreateCategoryRequest("Insurance")));

        var loanId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/buckets",
            new SaveSavingBucketRequest("Car loan", IsDebt: true, DebtBalance: 20000m, DebtRate: 6m, DebtInstallment: 600m)));
        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Car loan", "expense", "fixed", 700m, 10, rent, fund,
                LinkedDebtBucketId: loanId, ExcessCategoryId: insurance, ExcessLabel: "Health + property")));

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(700m))).EnsureSuccessStatusCode();

        var period = (await LoadAsync(client, account.Id)).CurrentPeriod!;
        var rows = period.Expenses.ToList();
        Assert.Equal(3, rows.Count);
        var extra = rows.Single(r => r.Part == FinApp.Domain.Budgeting.InstallmentPart.Additional);
        Assert.Equal(100m, extra.Amount.Amount);
        Assert.Equal(insurance, extra.CategoryId);
        // The loan was serviced at exactly the contractual figure, not at what left the account.
        Assert.Equal(600m, rows.Where(r => r.Part != FinApp.Domain.Budgeting.InstallmentPart.Additional)
            .Sum(r => r.Amount.Amount));
    }

    [Fact]
    public async Task Create_with_an_unknown_category_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_badcat");
        var account = await CreateAccount(client, "BadCat");
        var (_, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, Guid.NewGuid(), fund));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_with_an_unknown_kind_is_rejected()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_badkind");
        var account = await CreateAccount(client, "BadKind");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var resp = await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "nonsense", "fixed", 500m, 15, rent, fund));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>The view is what a thin client edits from: without the category/fund ids and AutoPost it can only
    /// show an item, never prefill an edit of one — matching by display name would be the alternative, and a rename
    /// or a duplicate name would silently retarget the save.</summary>
    [Fact]
    public async Task The_view_carries_what_an_edit_needs_to_prefill()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_viewedit");
        var account = await CreateAccount(client, "ViewEdit");
        var (rent, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund, AutoPost: true)));

        var row = (await client.GetFromJsonAsync<RecurringViewDto>($"/accounts/{account.Id}/recurring"))!.Items.Single();
        Assert.Equal(rent, row.CategoryId);
        Assert.Equal(fund, row.FundId);
        Assert.True(row.AutoPost);
    }

    /// <summary>The pickers travel with the view so an editor needs no second read — and they're there even for
    /// income, whose sources come from a different list than a bill's categories.</summary>
    [Fact]
    public async Task The_view_carries_the_editor_pickers()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_pickers");
        var account = await CreateAccount(client, "Pickers");
        var (rent, salary, fund) = await SeedAsync(client, account.Id, auth.UserId);

        var view = (await client.GetFromJsonAsync<RecurringViewDto>($"/accounts/{account.Id}/recurring"))!;
        Assert.Contains(view.Categories, c => c.Id == rent);
        Assert.Contains(view.ContributionCategories, c => c.Id == salary);
        Assert.Contains(view.Funds, f => f.Id == fund);
        Assert.DoesNotContain(view.Categories, c => c.Id == salary);   // a spend category is not an income source
    }

    [Fact]
    public async Task A_row_reports_whether_it_is_still_expected_this_period()
    {
        // O5 splits the list into "coming up" and "already this period", and Due/Upcoming cannot make that split:
        // an item due in three weeks is neither, and so is one that was posted this morning. Pending tells them apart.
        var (client, auth) = await _factory.RegisterAndAuthAsync("rc_pending");
        var account = await CreateAccount(client, "Pending");

        // ⚠️ Its own seed, on the CURRENT month, not SeedAsync's fixed Jan 2026. A new item records the day it was
        // created, and an item whose due date fell before it existed is not expected that period at all — so in a
        // period that is already in the past, nothing is ever pending and this test would assert on the calendar
        // rather than on the field. The due day is the last of the month, so "not yet due" holds on any run date.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = new DateOnly(today.Year, today.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var rent = agg.AddCategory("Rent").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(auth.UserId, "Me");
        agg.StartPeriod(from, to);
        (await client.PutAsJsonAsync($"/accounts/{account.Id}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();

        var recId = await IdOf(await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, to.Day, rent, fund)));

        var before = (await client.GetFromJsonAsync<RecurringViewDto>($"/accounts/{account.Id}/recurring"))!
            .Items.Single(r => r.Id == recId);
        Assert.True(before.Pending);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/recurring/{recId}/confirm",
            new ConfirmRecurringRequest(500m))).EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<RecurringViewDto>($"/accounts/{account.Id}/recurring"))!
            .Items.Single(r => r.Id == recId);
        Assert.False(after.Pending);   // handled — it belongs in the lower section now
    }

    [Fact]
    public async Task Stranger_cannot_create_a_recurring_item()
    {
        var (owner, auth) = await _factory.RegisterAndAuthAsync("rc_owner");
        var (stranger, _) = await _factory.RegisterAndAuthAsync("rc_intruder");
        var account = await CreateAccount(owner, "Private");
        var (rent, _, fund) = await SeedAsync(owner, account.Id, auth.UserId);

        var resp = await stranger.PostAsJsonAsync($"/accounts/{account.Id}/recurring",
            new AddRecurringRequest("Rent", "expense", "fixed", 500m, 15, rent, fund));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
