using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FinApp.Server.Tests;

/// <summary>
/// The feedback intake (OPEN-BETA B2). B1 catches crashes; this catches everything that isn't one — and it has
/// to work for someone who never signed up, because "I looked and didn't sign up, here's why" is feedback we can
/// get no other way.
/// </summary>
public class FeedbackApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public FeedbackApiTests(FinAppServerFactory factory) => _factory = factory;

    private async Task<int> CountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FeedbackService>().CountAsync();
    }

    [Fact]
    public async Task Someone_who_never_signed_up_can_still_send_feedback()
    {
        var client = _factory.CreateClient();
        var before = await CountAsync();

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(2, "Couldn't work out how to start a period.", Source: "landing"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(before + 1, await CountAsync());
    }

    [Fact]
    public async Task A_signed_in_user_can_send_feedback()
    {
        var (client, _) = await _factory.RegisterAndAuthAsync("fb_user");
        var before = await CountAsync();

        var resp = await client.PostAsJsonAsync("/feedback", new FeedbackRequest(5, "Debt payoff view is great."));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(before + 1, await CountAsync());
    }

    [Fact]
    public async Task A_rating_with_no_comment_is_valid_and_so_is_a_comment_with_no_rating()
    {
        var client = _factory.CreateClient();
        var before = await CountAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/feedback", new FeedbackRequest(Rating: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/feedback", new FeedbackRequest(Comment: "Just a note."))).StatusCode);

        Assert.Equal(before + 2, await CountAsync());
    }

    [Fact]
    public async Task An_empty_submission_is_accepted_but_stores_nothing()
    {
        // The form guards against this, but a stray POST must not create noise rows to sift through later.
        var client = _factory.CreateClient();
        var before = await CountAsync();

        var resp = await client.PostAsJsonAsync("/feedback", new FeedbackRequest(null, "   "));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task An_out_of_range_rating_is_dropped_rather_than_rejected()
    {
        // Never lose the comment over a bad number: the text is the valuable part.
        var client = _factory.CreateClient();
        var before = await CountAsync();

        var resp = await client.PostAsJsonAsync("/feedback",
            new FeedbackRequest(Rating: 99, Comment: "Rating widget sent nonsense but this text matters."));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(before + 1, await CountAsync());
    }

    [Fact]
    public async Task Consent_to_publish_defaults_to_false()
    {
        // A review is only ever quotable if the box was ticked for that review (OPEN-BETA P1). The default has to
        // be "no" at every layer, including a client that forgets to send the field at all.
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/feedback", new { comment = "No consent field in this payload." });

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.False(new FeedbackRequest().PublicConsent);
    }
}
