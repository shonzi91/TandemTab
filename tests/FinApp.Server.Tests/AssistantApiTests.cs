using System.Net;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Server.Assistant;
using FinApp.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinApp.Server.Tests;

/// <summary>
/// The assistant endpoint, driven with a <b>fake parser</b> — no test in this suite calls a model. What is under
/// test is the part that has to be right whatever the model says: the gates in front of it, and the validation
/// behind it.
/// <para>★ The rule those tests pin: <b>a reply that does not validate is not an error, it is
/// <c>unknown</c></b>. Suggestion chips are a fine answer; a broken screen is not.</para>
/// </summary>
public class AssistantApiTests : IClassFixture<FinAppServerFactory>
{
    private readonly FinAppServerFactory _factory;

    public AssistantApiTests(FinAppServerFactory factory) => _factory = factory;

    /// <summary>Answers whatever it is told to, so a test can pose the model's worst behaviour as easily as its best.</summary>
    private sealed class FakeParser(AssistantReplyDto? reply, bool available = true) : IAssistantParser
    {
        public int Calls { get; private set; }
        public AssistantAskRequest? LastRequest { get; private set; }
        public bool Available => available;

        public Task<AssistantReplyDto?> ParseAsync(AssistantAskRequest req, CancellationToken ct = default)
        {
            Calls++;
            LastRequest = req;
            return Task.FromResult(reply);
        }
    }

    private WebApplicationFactory<Program> WithParser(IAssistantParser parser) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IAssistantParser>();
            services.AddSingleton(parser);
            // AssistantService holds the per-user counters, so it must be rebuilt alongside the parser or a
            // previous test's daily tally would come with it.
            services.RemoveAll<AssistantService>();
            services.AddSingleton<AssistantService>();
        }));

    private static async Task<Guid> CreateAccountAsync(HttpClient client) =>
        (await (await client.PostAsJsonAsync("/accounts", new CreateAccountRequest("Main", "GBP")))
            .Content.ReadFromJsonAsync<AccountSummaryDto>())!.Id;

    private static Task ConsentAsync(HttpClient client, Guid accountId) =>
        client.PostAsJsonAsync("/consent", new RecordConsentRequest("ai_assistant", accountId, true));

    private static AssistantAskRequest Ask(string question, params string[] slots) => new(question, slots);

    [Fact]
    public async Task An_unauthenticated_ask_is_refused()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync($"/accounts/{Guid.NewGuid()}/assistant/ask", Ask("what is safe to spend"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Without_consent_the_model_is_never_reached()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "tab.goals", 0));
        using var app = WithParser(parser);
        var (client, _) = await _factory.RegisterAndAuthAsync("ai-noconsent");
        var accountId = await CreateAccountAsync(client);
        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var resp = await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", Ask("take me to my goals"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, parser.Calls);      // the point: consent gates the CALL, not just the answer
    }

    [Fact]
    public async Task Consent_for_one_account_does_not_carry_to_another()
    {
        // Shared accounts are shared money. One account being switched on is not a decision about the next one.
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "tab.goals", 0));
        using var app = WithParser(parser);
        var (client, _) = await _factory.RegisterAndAuthAsync("ai-twoaccounts");
        var first = await CreateAccountAsync(client);
        var second = await CreateAccountAsync(client);
        await ConsentAsync(client, first);

        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        Assert.Equal(HttpStatusCode.OK,
            (await scoped.PostAsJsonAsync($"/accounts/{first}/assistant/ask", Ask("take me to my goals"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await scoped.PostAsJsonAsync($"/accounts/{second}/assistant/ask", Ask("take me to my goals"))).StatusCode);
    }

    [Fact]
    public async Task With_no_key_configured_the_endpoint_says_so_rather_than_pretending()
    {
        using var app = WithParser(new FakeParser(null, available: false));
        var (client, _) = await _factory.RegisterAndAuthAsync("ai-unconfigured");
        var accountId = await CreateAccountAsync(client);
        await ConsentAsync(client, accountId);
        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        Assert.False((await scoped.GetFromJsonAsync<AssistantStatusDto>("/accounts/assistant/status"))!.Available);

        var resp = await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", Ask("what is safe to spend"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task A_valid_navigation_comes_back_intact()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "open.goal", 1));
        var reply = await AskWith(parser, "ai-nav", Ask("how is my {1} doing", AssistantSlotKinds.Goal));

        Assert.Equal(AssistantIntents.Navigate, reply.Intent);
        Assert.Equal("open.goal", reply.Target);
        Assert.Equal(1, reply.Slot);
        Assert.Equal("how is my {1} doing", parser.LastRequest!.Question);
    }

    [Fact]
    public async Task A_target_key_that_does_not_exist_becomes_unknown()
    {
        var reply = await AskWith(new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "open.spaceship", 0)),
            "ai-badkey", Ask("fly me to the moon"));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
        Assert.Null(reply.Target);
    }

    [Fact]
    public async Task A_key_from_the_wrong_catalogue_becomes_unknown()
    {
        // "explain.runway" is a real key — as an EXPLAINER. Returned under navigate it is a screen that does not exist.
        var reply = await AskWith(new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "explain.runway", 0)),
            "ai-wrongcat", Ask("how does runway work"));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
    }

    [Fact]
    public async Task An_entity_target_without_its_entity_becomes_unknown()
    {
        var reply = await AskWith(new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "open.goal", 0)),
            "ai-noslot", Ask("open my goal"));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
    }

    [Fact]
    public async Task An_entity_target_pointed_at_the_wrong_kind_of_entity_becomes_unknown()
    {
        // ★ The sharpest of these: "open.goal" with a WALLET in the slot would open a goal screen keyed on a
        // wallet's id. It fails cleanly here rather than interestingly three layers later.
        var reply = await AskWith(new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "open.goal", 1)),
            "ai-wrongkind", Ask("open my {1}", AssistantSlotKinds.Wallet));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
    }

    [Fact]
    public async Task A_slot_index_past_the_end_becomes_unknown()
    {
        var reply = await AskWith(new FakeParser(new AssistantReplyDto(AssistantIntents.Navigate, "open.goal", 4)),
            "ai-badslot", Ask("open my {1}", AssistantSlotKinds.Goal));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
    }

    [Fact]
    public async Task A_failed_model_call_becomes_unknown_rather_than_an_error()
    {
        var reply = await AskWith(new FakeParser(null), "ai-nullreply", Ask("what is safe to spend"));

        Assert.Equal(AssistantIntents.Unknown, reply.Intent);
    }

    [Fact]
    public async Task An_ill_formed_question_is_answered_without_calling_the_model()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Report, "report.spent", 0));

        // A placeholder with no slot behind it, an unknown slot kind, and an over-long question: all cheap
        // to refuse, and refusing them without a call is also what makes a probing client cost nothing.
        foreach (var bad in new[]
                 {
                     Ask("how is my {2} doing", AssistantSlotKinds.Goal),
                     Ask("how is my {1} doing", "merchant"),
                     Ask(new string('a', AssistantService.MaxQuestionLength + 1)),
                 })
        {
            var reply = await AskWith(parser, $"ai-illformed{Guid.NewGuid():N}"[..14], bad);
            Assert.Equal(AssistantIntents.Unknown, reply.Intent);
        }

        Assert.Equal(0, parser.Calls);
    }

    [Fact]
    public async Task The_same_question_twice_costs_one_call()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Report, "report.spent", 0));
        using var app = WithParser(parser);
        var (client, _) = await _factory.RegisterAndAuthAsync("ai-cache");
        var accountId = await CreateAccountAsync(client);
        await ConsentAsync(client, accountId);
        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        for (var i = 0; i < 3; i++)
            await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", Ask("what have I spent"));

        Assert.Equal(1, parser.Calls);
    }

    [Fact]
    public async Task Past_the_daily_cap_the_answer_is_a_refusal_not_a_call()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Report, "report.spent", 0));
        using var app = WithParser(parser);
        var (client, _) = await _factory.RegisterAndAuthAsync("ai-cap");
        var accountId = await CreateAccountAsync(client);
        await ConsentAsync(client, accountId);
        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        HttpResponseMessage? last = null;
        for (var i = 0; i <= AssistantService.DailyCap; i++)
            // A distinct question each time, or the cache would answer and never reach the counter.
            last = await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", Ask($"question number {i} please"));

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal(AssistantService.DailyCap, parser.Calls);
    }

    [Fact]
    public async Task A_free_plan_is_refused_with_the_feature_key_the_client_needs()
    {
        var parser = new FakeParser(new AssistantReplyDto(AssistantIntents.Report, "report.spent", 0));
        using var app = WithParser(parser);
        var (client, auth) = await _factory.RegisterAndAuthAsync("ai-free");
        var accountId = await CreateAccountAsync(client);
        await ConsentAsync(client, accountId);

        // Pin the plan rather than flipping the global flag: the suite's users are all in the beta cohort, so
        // "Monetization on" would make them Pro and prove nothing.
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PlanOverrideService>().SetAsync(auth.UserId, "free");

        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var resp = await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", Ask("what have I spent"));

        Assert.Equal(HttpStatusCode.PaymentRequired, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PaymentRequiredBody>();
        Assert.Equal(PlanFeatures.Assistant, body!.feature);
        Assert.Equal(0, parser.Calls);
    }

    private sealed record PaymentRequiredBody(string error, string feature);

    /// <summary>Register, create an account, consent, ask once — the shape every validation test wants.</summary>
    private async Task<AssistantReplyDto> AskWith(IAssistantParser parser, string username, AssistantAskRequest request)
    {
        using var app = WithParser(parser);
        var (client, _) = await _factory.RegisterAndAuthAsync(username);
        var accountId = await CreateAccountAsync(client);
        await ConsentAsync(client, accountId);
        var scoped = app.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var resp = await scoped.PostAsJsonAsync($"/accounts/{accountId}/assistant/ask", request);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AssistantReplyDto>())!;
    }
}
