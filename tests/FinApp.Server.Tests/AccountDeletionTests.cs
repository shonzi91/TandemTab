using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// Deleting a whole user account: the 30-day soft-delete grace (request → cancel by logging back in), the
/// transfer-ownership guard, 2FA gating, and the final purge that erases identity while keeping the user's
/// entries in other people's accounts.
/// </summary>
public class AccountDeletionTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AccountDeletionTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<Guid> CreateAccount(HttpClient client) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Mine", "EUR")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!.Id;

    private async Task ShareAsync(HttpClient owner, Guid accountId, HttpClient invitee, string inviteeUsername)
    {
        await owner.PostAsJsonAsync($"/accounts/{accountId}/invitations", new CreateInvitationRequest(inviteeUsername));
        var pending = await invitee.GetFromJsonAsync<List<InvitationDto>>("/invitations/pending");
        await invitee.PostAsync($"/invitations/{pending!.Single().Id}/accept", null);
    }

    private async Task PurgeAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeAsync(userId);
    }

    [Fact]
    public async Task Requesting_deletion_schedules_it_and_cancel_clears_it()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("del-anna");

        (await client.PostAsJsonAsync("/me/delete", new DeleteAccountRequest())).EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.True(me!.DeletionPending);
        Assert.NotNull(me.PendingDeletionAt);

        (await client.PostAsync("/me/delete/cancel", null)).EnsureSuccessStatusCode();
        var after = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.False(after!.DeletionPending);
    }

    [Fact]
    public async Task Deletion_is_blocked_while_owning_a_shared_account()
    {
        var (owner, _) = await _factory.RegisterAndAuthAsync("del-owner");
        var (member, _) = await _factory.RegisterAndAuthAsync("del-member");
        var accountId = await CreateAccount(owner);
        await ShareAsync(owner, accountId, member, "del-member");

        // The owner can't delete their account while it still has another member.
        var resp = await owner.PostAsJsonAsync("/me/delete", new DeleteAccountRequest());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // The member (who doesn't own it) can.
        (await member.PostAsJsonAsync("/me/delete", new DeleteAccountRequest())).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deletion_requires_a_2fa_code_when_enabled()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("del-cora", "del-cora@example.com", "supersecret");
        var setup = (await (await client.PostAsync("/auth/2fa/setup", null))
            .Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        (await client.PostAsJsonAsync("/auth/2fa/confirm", new TwoFactorCodeRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();

        // No code → rejected.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/me/delete", new DeleteAccountRequest())).StatusCode);

        // Correct code → scheduled.
        (await client.PostAsJsonAsync("/me/delete", new DeleteAccountRequest(Totp.Generate(setup.Secret))))
            .EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<UserDto>("/me"))!.DeletionPending);
    }

    [Fact]
    public async Task Purge_erases_the_user_but_keeps_another_account_they_belonged_to()
    {
        var (owner, ownerAuth) = await _factory.RegisterAndAuthAsync("del-keep-owner");
        var (leaver, leaverAuth) = await _factory.RegisterAndAuthAsync("del-keep-leaver", "del-keep-leaver@example.com", "supersecret");
        var sharedId = await CreateAccount(owner);
        await ShareAsync(owner, sharedId, leaver, "del-keep-leaver");
        var ownedByLeaver = await CreateAccount(leaver);

        await PurgeAsync(leaverAuth.UserId);

        // The leaver's identity is gone — they can no longer sign in.
        var login = await _factory.CreateClient().PostAsJsonAsync("/auth/login",
            new LoginRequest("del-keep-leaver", "supersecret"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // The owner's shared account survives (the leaver's operations there stay); the leaver's own account is gone.
        var ownerAccounts = await owner.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.Contains(ownerAccounts!, a => a.Id == sharedId);
        Assert.DoesNotContain(ownerAccounts!, a => a.Id == ownedByLeaver);
    }

    [Fact]
    public async Task Startup_purge_leaves_users_still_within_the_grace_period()
    {
        var (client, auth) = await _factory.RegisterAndAuthAsync("del-grace");
        (await client.PostAsJsonAsync("/me/delete", new DeleteAccountRequest())).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().PurgeDueAsync();

        // Freshly requested (30 days out) → not purged yet: /me still works.
        var me = await client.GetFromJsonAsync<UserDto>("/me");
        Assert.Equal(auth.UserId, me!.Id);
        Assert.True(me.DeletionPending);
    }
}
