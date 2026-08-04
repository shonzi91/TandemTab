using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// The beta-cohort signup stamp (OPEN-BETA B4). The one fact that can't be backfilled: who joined, and when.
/// The <c>User</c> row carries no creation timestamp, so this side table is the only record of it.
/// </summary>
public class SignupStampTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public SignupStampTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<int> CountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SignupService>().CountAsync();
    }

    [Fact]
    public async Task Registering_stamps_the_signup()
    {
        var before = await CountAsync();

        await _factory.RegisterAndAuthAsync("cohort_user");

        Assert.Equal(before + 1, await CountAsync());
    }
}
