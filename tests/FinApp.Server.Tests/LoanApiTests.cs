using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>Loans are Forecasts-tab data: a standalone, contributor-scoped store that never touches the money model.</summary>
public class LoanApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public LoanApiTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid AccountId)> AccountAsync(string user)
    {
        var (client, _) = await _factory.RegisterAndAuthAsync(user);
        var id = (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Main", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!.Id;
        return (client, id);
    }

    [Fact]
    public async Task Loans_can_be_added_updated_and_removed()
    {
        var (client, accountId) = await AccountAsync("loan-anna");

        var created = await (await client.PostAsJsonAsync($"/accounts/{accountId}/loans",
            new SaveLoanRequest("Car", 10_000m, 6.5m, 300m))).Content.ReadFromJsonAsync<LoanDto>();
        Assert.Equal("Car", created!.Name);
        Assert.Equal("EUR", created.Currency);

        var list = await client.GetFromJsonAsync<List<LoanDto>>($"/accounts/{accountId}/loans");
        Assert.Single(list!);

        (await client.PutAsJsonAsync($"/accounts/{accountId}/loans/{created.Id}",
            new SaveLoanRequest("Car loan", 9_500m, 6.5m, 350m))).EnsureSuccessStatusCode();
        Assert.Equal(9_500m, (await client.GetFromJsonAsync<List<LoanDto>>($"/accounts/{accountId}/loans"))!.Single().Balance);

        (await client.DeleteAsync($"/accounts/{accountId}/loans/{created.Id}")).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<List<LoanDto>>($"/accounts/{accountId}/loans"))!);
    }

    [Fact]
    public async Task A_non_member_cannot_see_an_accounts_loans()
    {
        var (owner, accountId) = await AccountAsync("loan-owner");
        await owner.PostAsJsonAsync($"/accounts/{accountId}/loans", new SaveLoanRequest("Car", 1000m, 5m, 100m));

        var (stranger, _) = await _factory.RegisterAndAuthAsync("loan-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/accounts/{accountId}/loans")).StatusCode);
    }
}
