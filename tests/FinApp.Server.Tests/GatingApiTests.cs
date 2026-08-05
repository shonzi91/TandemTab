using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace FinApp.Server.Tests;

/// <summary>Test host with one admin address configured, so a test can pin its own account to Free/Pro through the
/// real admin override and exercise the server-side paywall exactly as production would (OPEN-BETA P4, Session 87).
/// The global monetization flag stays off — a plan override alone makes monetization live for that one account.</summary>
public sealed class GatingServerFactory : FinAppServerFactory
{
    public const string AdminEmail = "gate.admin@example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Admin:Emails", AdminEmail);   // overrides the base's empty allowlist
    }
}

/// <summary>
/// The server-side half of the paywall. A Free plan must be refused at the endpoints that carry a real action —
/// sharing (invite), statement import, and the 2nd account (Free = 1). The refusal is a 402 naming the blocked
/// feature, so the client can raise the same upgrade prompt a local gate would. Pinning Pro lets the same calls
/// through. This is what makes "test Free/Pro" prod-like: the gates aren't cosmetic.
/// </summary>
public class GatingApiTests : IClassFixture<GatingServerFactory>
{
    private readonly GatingServerFactory _factory;
    public GatingApiTests(GatingServerFactory factory) => _factory = factory;

    private record GateError(string Error, string? Feature);

    [Fact]
    public async Task Free_is_refused_server_side_and_Pro_passes()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("gateboss", GatingServerFactory.AdminEmail);

        // Pin Free — monetization goes live for this account only; plan resolves to "free".
        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("free"))).EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.True(me!.MonetizationEnabled);
        Assert.Equal("free", me.Plan);

        // The 1st account is within the Free allowance…
        var first = await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Main", "EUR"));
        first.EnsureSuccessStatusCode();
        var acct = (await first.Content.ReadFromJsonAsync<AccountSummaryDto>())!;

        // …the 2nd is not.
        var second = await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Second", "EUR"));
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Equal(PlanFeatures.Caps, (await second.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // Import and invite are Pro; the gate fires before any mutation, so an empty/irrelevant body still 402s.
        var import = await client.PostAsJsonAsync($"/accounts/{acct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.Equal(HttpStatusCode.PaymentRequired, import.StatusCode);
        Assert.Equal(PlanFeatures.Import, (await import.Content.ReadFromJsonAsync<GateError>())!.Feature);

        var invite = await client.PostAsJsonAsync($"/accounts/{acct.Id}/invitations", new CreateInvitationRequest("nobody"));
        Assert.Equal(HttpStatusCode.PaymentRequired, invite.StatusCode);
        Assert.Equal(PlanFeatures.Share, (await invite.Content.ReadFromJsonAsync<GateError>())!.Feature);

        // Pin Pro — the same gated actions now pass (a 2nd account is created; import no longer 402s).
        (await client.PostAsJsonAsync("/admin/plan-override", new PlanOverrideRequest("pro"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Second", "EUR"))).EnsureSuccessStatusCode();
        var importPro = await client.PostAsJsonAsync($"/accounts/{acct.Id}/import",
            new ImportTransactionsRequest(Array.Empty<ImportRowDto>()));
        Assert.NotEqual(HttpStatusCode.PaymentRequired, importPro.StatusCode);
    }
}
