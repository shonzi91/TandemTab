using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;

namespace FinApp.Server.Tests;

/// <summary>
/// The savings <b>movement</b> read model. Three endpoints could create a movement and one could undo it, but no
/// read returned any — so a thin client had nothing to draw an undo against and the delete route was unreachable by
/// construction. These pin what the list contains, how each kind names its counterpart, and the one thing a client
/// must not have to guess: whether the undo will actually be accepted.
/// </summary>
public class SavingMovementsViewApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SavingMovementsViewApiTests(FinAppServerFactory factory) => _factory = factory;

    private static readonly DateOnly When = new(2026, 1, 10);

    private static async Task<AccountSummaryDto> CreateAccount(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest(name, "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!;

    private static async Task<(Guid Food, Guid A, Guid B, Guid Fund)> SeedAsync(HttpClient client, Guid accountId, Guid memberId)
    {
        var agg = new Account("Seed", "EUR");
        agg.AddDefaultFunds();
        var food = agg.AddCategory("Food").Id;
        var a = agg.AddSavingCategory("A").Id;
        var b = agg.AddSavingCategory("B").Id;
        var fund = agg.FundId("Bank");
        agg.AddMember(memberId, "Me");
        var period = agg.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(memberId, new Money(1000m, "EUR"), fundId: fund);
        period.AllocateToSavings(a, new Money(400m, "EUR"), When);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/snapshot",
            new SaveAccountRequest(AccountSnapshotSerializer.Serialize(agg), 0))).EnsureSuccessStatusCode();
        return (food, a, b, fund);
    }

    private static Task<SavingsViewDto?> Savings(HttpClient client, Guid accountId) =>
        client.GetFromJsonAsync<SavingsViewDto>($"/accounts/{accountId}/savings");

    /// <summary>A plain deposit belongs to the deposits list and must not be counted twice as a movement.</summary>
    [Fact]
    public async Task A_deposit_is_not_a_movement()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mvview_deposit");
        var account = await CreateAccount(client, "Mv");
        await SeedAsync(client, account.Id, auth.UserId);

        var view = (await Savings(client, account.Id))!;

        Assert.Single(view.Deposits);
        Assert.Empty(view.MovementRows);
    }

    [Fact]
    public async Task A_budget_move_names_the_category_it_matured_into_and_can_be_undone()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mvview_tobudget");
        var account = await CreateAccount(client, "Mv");
        var (food, a, _, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/to-budget",
            new ConvertSavingToBudgetRequest(a, food, 120m, When))).EnsureSuccessStatusCode();

        var row = Assert.Single((await Savings(client, account.Id))!.MovementRows);
        Assert.Equal("to-budget", row.Kind);
        Assert.Equal("Food", row.Counterpart);
        Assert.Equal(120m, row.Amount);   // positive: the direction is the kind, not the sign
        Assert.True(row.Undoable);

        (await client.DeleteAsync($"/accounts/{account.Id}/savings/movements/{row.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await Savings(client, account.Id))!.MovementRows);
    }

    /// <summary>Both halves of a transfer are listed — a bucket that gained money should say so — but only the
    /// outgoing one offers the undo, because removing either reverses the whole pair.</summary>
    [Fact]
    public async Task A_transfer_lists_both_halves_and_only_the_outgoing_one_is_undoable()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mvview_transfer");
        var account = await CreateAccount(client, "Mv");
        var (_, a, b, _) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/transfer",
            new MoveSavingsRequest(a, b, 90m, When))).EnsureSuccessStatusCode();

        var rows = (await Savings(client, account.Id))!.MovementRows;
        var outgoing = Assert.Single(rows, r => r.Kind == "transfer-out");
        var incoming = Assert.Single(rows, r => r.Kind == "transfer-in");

        Assert.Equal("B", outgoing.Counterpart);   // each half names the bucket at the other end
        Assert.Equal("A", incoming.Counterpart);
        Assert.Equal(90m, outgoing.Amount);
        Assert.True(outgoing.Undoable);
        Assert.False(incoming.Undoable);

        (await client.DeleteAsync($"/accounts/{account.Id}/savings/movements/{outgoing.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await Savings(client, account.Id))!.MovementRows);   // the pair goes together
    }

    [Fact]
    public async Task A_disbursement_is_listed_and_can_be_undone()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mvview_disburse");
        var account = await CreateAccount(client, "Mv");
        var (_, a, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/disburse",
            new DisburseSavingRequest(a, fund, 250m, When))).EnsureSuccessStatusCode();

        var row = Assert.Single((await Savings(client, account.Id))!.MovementRows);
        Assert.Equal("disbursed", row.Kind);
        Assert.Null(row.Counterpart);   // it left savings altogether — there is no other end to name
        Assert.Equal(250m, row.Amount);
        Assert.True(row.Undoable);

        (await client.DeleteAsync($"/accounts/{account.Id}/savings/movements/{row.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await Savings(client, account.Id))!.MovementRows);
    }

    /// <summary>
    /// ★ The row the flag exists for. Spending savings writes a drawdown linked to an expense, and
    /// <c>RemoveSavingMovement</c> refuses exactly that shape — it is undone by deleting the expense. A client that
    /// inferred "undoable" from "is a movement" would render a control whose only possible outcome is a 400.
    /// </summary>
    [Fact]
    public async Task Savings_spent_through_an_expense_is_listed_but_not_undoable_here()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("mvview_spent");
        var account = await CreateAccount(client, "Mv");
        var (food, a, _, fund) = await SeedAsync(client, account.Id, auth.UserId);

        (await client.PostAsJsonAsync($"/accounts/{account.Id}/savings/spend",
            new SpendFromSavingsRequest(a, food, 60m, When, fund))).EnsureSuccessStatusCode();

        var row = Assert.Single((await Savings(client, account.Id))!.MovementRows);
        Assert.Equal("spent", row.Kind);
        Assert.Equal("Food", row.Counterpart);
        Assert.False(row.Undoable);

        // And the flag is honest: the endpoint really does refuse it.
        var undo = await client.DeleteAsync($"/accounts/{account.Id}/savings/movements/{row.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, undo.StatusCode);
    }
}
