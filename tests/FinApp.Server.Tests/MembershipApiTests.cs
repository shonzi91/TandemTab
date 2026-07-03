using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// Leaving accounts, owner-driven member removal + ownership transfer, and the archive/reactivate grace-period
/// flow when the last member leaves.
/// </summary>
public class MembershipApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public MembershipApiTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<Guid> CreateAccount(HttpClient client, string name = "Shared")
    {
        var created = await (await client.PostAsJsonAsync("/accounts",
            new CreateAccountRequest(name, "EUR"))).Content.ReadFromJsonAsync<AccountSummaryDto>();
        return created!.Id;
    }

    /// <summary>Share owner's account with a second user by inviting + accepting.</summary>
    private async Task ShareAsync(HttpClient owner, Guid accountId, HttpClient invitee, string inviteeUsername)
    {
        await owner.PostAsJsonAsync($"/accounts/{accountId}/invitations", new CreateInvitationRequest(inviteeUsername));
        var pending = await invitee.GetFromJsonAsync<List<InvitationDto>>("/invitations/pending");
        await invitee.PostAsync($"/invitations/{pending!.Single().Id}/accept", null);
    }

    [Fact]
    public async Task Sole_member_leaving_archives_the_account()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("solo1");
        var id = await CreateAccount(client);

        var resp = await client.PostAsJsonAsync($"/accounts/{id}/leave", new LeaveAccountRequest());
        resp.EnsureSuccessStatusCode();

        // Gone from the active list, present in the archived list.
        var active = await client.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.DoesNotContain(active!, a => a.Id == id);
        var archived = await client.GetFromJsonAsync<List<ArchivedAccountDto>>("/accounts/archived");
        Assert.Contains(archived!, a => a.Id == id);
    }

    [Fact]
    public async Task Deleting_an_account_archives_it_recoverably()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("del1");
        var id = await CreateAccount(client);

        (await client.DeleteAsync($"/accounts/{id}")).EnsureSuccessStatusCode();

        // Delete is a soft delete: hidden from the active list but recoverable from the archived list for 30 days.
        var active = await client.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.DoesNotContain(active!, a => a.Id == id);
        var archived = await client.GetFromJsonAsync<List<ArchivedAccountDto>>("/accounts/archived");
        Assert.Contains(archived!, a => a.Id == id);
    }

    [Fact]
    public async Task Archived_account_can_be_reactivated()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("solo2");
        var id = await CreateAccount(client);
        await client.PostAsJsonAsync($"/accounts/{id}/leave", new LeaveAccountRequest());

        var resp = await client.PostAsync($"/accounts/{id}/reactivate", null);
        resp.EnsureSuccessStatusCode();

        var active = await client.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.Contains(active!, a => a.Id == id);
        var archived = await client.GetFromJsonAsync<List<ArchivedAccountDto>>("/accounts/archived");
        Assert.DoesNotContain(archived!, a => a.Id == id);
    }

    [Fact]
    public async Task Non_owner_can_leave_a_shared_account()
    {
        var (owner, _) = await _factory.RegisterAndAuthAsync("owner_leave");
        var (member, memberAuth) = await _factory.RegisterAndAuthAsync("member_leave");
        var id = await CreateAccount(owner);
        await ShareAsync(owner, id, member, "member_leave");

        var resp = await member.PostAsJsonAsync($"/accounts/{id}/leave", new LeaveAccountRequest());
        resp.EnsureSuccessStatusCode();

        var memberList = await member.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.DoesNotContain(memberList!, a => a.Id == id);
        // Owner still has it, now with a single member.
        var ownerList = await owner.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.Single(ownerList!.Single(a => a.Id == id).Members);
    }

    [Fact]
    public async Task Owner_leaving_requires_a_new_owner_then_transfers()
    {
        var (owner, _) = await _factory.RegisterAndAuthAsync("owner_x1");
        var (member, memberAuth) = await _factory.RegisterAndAuthAsync("member_x1");
        var id = await CreateAccount(owner);
        await ShareAsync(owner, id, member, "member_x1");

        // Without naming a successor → 400.
        var bad = await owner.PostAsJsonAsync($"/accounts/{id}/leave", new LeaveAccountRequest());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Naming the remaining member transfers ownership and removes the old owner.
        var ok = await owner.PostAsJsonAsync($"/accounts/{id}/leave", new LeaveAccountRequest(memberAuth.UserId));
        ok.EnsureSuccessStatusCode();

        var memberView = (await member.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts"))!.Single(a => a.Id == id);
        Assert.True(memberView.IsOwner);
        Assert.Single(memberView.Members);
    }

    [Fact]
    public async Task Owner_can_remove_a_member()
    {
        var (owner, _) = await _factory.RegisterAndAuthAsync("owner_rm");
        var (member, memberAuth) = await _factory.RegisterAndAuthAsync("member_rm");
        var id = await CreateAccount(owner);
        await ShareAsync(owner, id, member, "member_rm");

        var resp = await owner.DeleteAsync($"/accounts/{id}/members/{memberAuth.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var memberList = await member.GetFromJsonAsync<List<AccountSummaryDto>>("/accounts");
        Assert.DoesNotContain(memberList!, a => a.Id == id);
    }

    [Fact]
    public async Task Non_owner_cannot_remove_members()
    {
        var (owner, ownerAuth) = await _factory.RegisterAndAuthAsync("owner_guard");
        var (member, _) = await _factory.RegisterAndAuthAsync("member_guard");
        var id = await CreateAccount(owner);
        await ShareAsync(owner, id, member, "member_guard");

        var resp = await member.DeleteAsync($"/accounts/{id}/members/{ownerAuth.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
