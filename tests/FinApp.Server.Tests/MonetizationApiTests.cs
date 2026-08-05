using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// Monetization ships as rails behind a flag (OPEN-BETA P4). The test host sets no <c>Monetization:Enabled</c>,
/// so the flag is off — which must mean no plan UI and no gating: every account reads "unlimited".
/// </summary>
public class MonetizationApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public MonetizationApiTests(FinAppServerFactory factory) => _factory = factory;

    [Fact]
    public async Task With_the_flag_off_the_user_is_unlimited_and_no_plan_ui_is_signalled()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("plan_user");

        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.NotNull(me);
        Assert.False(me!.MonetizationEnabled);
        Assert.Equal("unlimited", me.Plan);

        var plans = await client.GetFromJsonAsync<PlansDto>("/plans");
        Assert.NotNull(plans);
        Assert.False(plans!.Enabled);
        Assert.Equal("unlimited", plans.CurrentPlan);
    }
}
