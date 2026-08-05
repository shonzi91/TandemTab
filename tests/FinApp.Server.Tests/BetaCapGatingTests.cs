using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace FinApp.Server.Tests;

/// <summary>Test host with the lifetime allowance set to a single seat (and monetization still OFF), so the second
/// real sign-up is a post-cap Free user during beta. Proves the model from Session 87: gating follows the PLAN,
/// not the global flag — the lifetime cohort stays unlimited/crowned while everyone after the cap gets the real
/// Free experience, even with billing off.</summary>
public sealed class BetaCapFactory : FinAppServerFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Beta:Cap", "1");                          // one lifetime-Pro seat; everyone after is Free
        builder.UseSetting("Beta:CountFrom", "2020-01-01T00:00:00Z"); // count every sign-up this suite makes
        // Monetization stays OFF (inherited) — the whole point is that post-cap users are gated with billing off.
    }
}

public class BetaCapGatingTests : IClassFixture<BetaCapFactory>
{
    private readonly BetaCapFactory _factory;
    public BetaCapGatingTests(BetaCapFactory factory) => _factory = factory;

    private record GateError(string Error, string? Feature);

    private static async Task<AccountSummaryDto> CreateAccountAsync(HttpClient c)
    {
        var resp = await c.PostAsJsonAsync("/accounts", new CreateAccountRequest("Main", "EUR"));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AccountSummaryDto>())!;
    }

    [Fact]
    public async Task Post_cap_users_get_the_Free_experience_while_the_lifetime_cohort_stays_unlimited()
    {
        // First real sign-up takes the single lifetime seat → unlimited, crowned, ungated.
        var (first, _) = await _factory.RegisterAndAuthAsync("betafirst", "betafirst@example.com");
        var meFirst = await first.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("unlimited", meFirst!.Plan);
        Assert.True(meFirst.ProBadge);
        Assert.False(meFirst.MonetizationEnabled);            // billing surfaces stay off during beta
        var firstAcct = await CreateAccountAsync(first);
        var firstImport = await first.PostAsJsonAsync($"/accounts/{firstAcct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.NotEqual(HttpStatusCode.PaymentRequired, firstImport.StatusCode);   // ungated

        // Second sign-up is past the cap → Free: gated, no crown, even though monetization is OFF.
        var (second, _) = await _factory.RegisterAndAuthAsync("betasecond", "betasecond@example.com");
        var meSecond = await second.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal("free", meSecond!.Plan);
        Assert.False(meSecond.ProBadge);
        Assert.False(meSecond.MonetizationEnabled);

        var secondAcct = await CreateAccountAsync(second);   // 1st account is within Free
        var import = await second.PostAsJsonAsync($"/accounts/{secondAcct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.Equal(HttpStatusCode.PaymentRequired, import.StatusCode);
        Assert.Equal(PlanFeatures.Import, (await import.Content.ReadFromJsonAsync<GateError>())!.Feature);

        var secondAccount = await second.PostAsJsonAsync("/accounts", new CreateAccountRequest("Second", "EUR"));
        Assert.Equal(HttpStatusCode.PaymentRequired, secondAccount.StatusCode);   // 2nd account gated too
    }
}
