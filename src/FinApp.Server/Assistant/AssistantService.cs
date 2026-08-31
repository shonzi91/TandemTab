using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FinApp.Contracts;
using FinApp.Server.Infrastructure;

namespace FinApp.Server.Assistant;

/// <summary>
/// Everything between the endpoint and the model: input limits, the per-user daily cap, a small answer cache, and
/// — the part that matters — <b>validating what comes back</b>.
/// <para>
/// ★ The rule the whole class turns on: <b>a reply that does not validate is not an error, it is
/// <c>unknown</c></b>. The client's answer to unknown is a row of suggestion chips, which is a perfectly good
/// outcome. Throwing instead would turn a model's bad day into a broken screen.
/// </para>
/// </summary>
public sealed partial class AssistantService(
    IAssistantParser parser,
    IServiceScopeFactory scopes,
    IConfiguration config,
    Health.HealthSignals signals,
    ILogger<AssistantService> log)
{
    /// <summary>Long enough for a real spoken question, short enough that this endpoint is not a way to push text
    /// through someone else's API key.</summary>
    public const int MaxQuestionLength = 240;

    /// <summary>More placeholders than this means the masker matched half the sentence, which is not a question.</summary>
    public const int MaxSlots = 8;

    /// <summary>
    /// The hard monthly ceiling on model calls per user — the actual spend limit.
    /// <para>★ <b>Why 300 is the default.</b> A question that reaches the model costs roughly $0.002 on Haiku, so
    /// 300 is about $0.60 a month against €2.50 of Pro revenue on the annual plan — a fifth of it, in the worst
    /// case, for a user who never stops asking. And because the local matcher answers the ordinary questions
    /// before this counter is ever touched, 300 model calls is a great many <em>unusual</em> questions.</para>
    /// </summary>
    public int MonthlyCap => config.GetValue("Assistant:MonthlyCallCap", 300);

    /// <summary>A day's share, so a month's budget cannot be spent in one afternoon. Blast radius, not budget.</summary>
    public int DailyCap => config.GetValue("Assistant:DailyCallCap", 50);

    private static readonly AssistantReplyDto UnknownReply = new(AssistantIntents.Unknown, null, null);

    // Keyed per user, deliberately. A masked question carries nothing personal — but the masker cannot mask a word
    // it does not recognise, so an unmatched noun ("at the corner shop") does travel as text. Caching that across
    // users would make one person's typing visible in another person's latency. Per-user, it is only ever their own.
    private readonly ConcurrentDictionary<(Guid User, string Key), AssistantReplyDto> _cache = new();

    public bool Available => parser.Available;

    /// <summary>How many model calls this user has left this month. Surfaced so the cap is something a person can
    /// see coming rather than walk into.</summary>
    public async Task<int> RemainingThisMonthAsync(Guid userId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AssistantUsageStore>();
        var used = await store.GetAsync(userId, AssistantUsageStore.MonthBucket(DateTimeOffset.UtcNow), ct);
        return Math.Max(0, MonthlyCap - used);
    }

    public async Task<AssistantReplyDto> AskAsync(Guid userId, AssistantAskRequest req, CancellationToken ct = default)
    {
        if (!parser.Available)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "The assistant is not available right now.");

        if (!IsWellFormed(req)) return UnknownReply;

        // ★ Before the counter, deliberately: a repeat of a question this user already asked is free, so it must
        // not consume budget. The same reasoning covers everything the client answered locally — it never arrives.
        var key = CacheKey(req);
        if (_cache.TryGetValue((userId, key), out var cached)) return cached;

        // ⚠️⚠️ Everything from here is inside the failure counter, and that is a correction, not a flourish. The
        // first production ask threw in ChargeAsync — BEFORE the parser was reached — so no AssistantCallFailed
        // was ever recorded, and the watchdog cheerfully logged "assistant 0/0 failed" while every question in
        // production was returning a 500. A health signal that only covers the half of a path somebody thought
        // would break is the same blind spot this whole feature was built to close.
        // ApiException is excluded on purpose: a 429 at the cap and a 503 with no key are deliberate refusals
        // working exactly as designed, and counting them would fire the alarm on correct behaviour.
        try
        {
            await ChargeAsync(userId, ct);
            return await ParseAndCacheAsync(userId, key, req, ct);
        }
        catch (ApiException) { throw; }
        catch
        {
            signals.Record(Health.HealthSignal.AssistantCallFailed);
            throw;
        }
    }

    private async Task<AssistantReplyDto> ParseAndCacheAsync(
        Guid userId, string key, AssistantAskRequest req, CancellationToken ct)
    {
        var reply = Validate(await parser.ParseAsync(req, ct), req);
        if (_cache.Count < 5_000) _cache[(userId, key)] = reply;

        // The only place the local hit rate is observable. Read it as "for every N answered free on the device,
        // one was paid for here" — it is what says whether the model call is still earning its keep.
        log.LogInformation("Assistant: answered by the model ({LocalHits} answered locally since the last one, intent {Intent}).",
            req.LocalHits, reply.Intent);
        return reply;
    }

    /// <summary>
    /// Spend one call against both ceilings, or refuse.
    /// <para>★ <b>Increment first, then compare</b> — the opposite way round would let two instances read the same
    /// "one left" and both spend it. The cost of this order is that an attempt refused at the cap still counts, so
    /// the stored number drifts above the true call count <em>for a user already at their limit</em> — who by
    /// definition is not spending anything more. Below the cap the two are identical, which is the range the
    /// figure is read in.</para>
    /// <para>⚠️ The month is checked before the day, so the message names the limit that actually bites.</para>
    /// </summary>
    private async Task ChargeAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AssistantUsageStore>();

        var month = await store.BumpAsync(userId, AssistantUsageStore.MonthBucket(now), ct);
        if (month > MonthlyCap)
        {
            log.LogInformation("Assistant: user hit the monthly cap of {Cap}.", MonthlyCap);
            throw new ApiException(StatusCodes.Status429TooManyRequests,
                "You've used all of this month's assistant questions. It resets on the 1st — everything the app can " +
                "answer on its own still works.");
        }

        var day = await store.BumpAsync(userId, AssistantUsageStore.DayBucket(now), ct);
        if (day > DailyCap)
        {
            log.LogInformation("Assistant: user hit the daily cap of {Cap}.", DailyCap);
            throw new ApiException(StatusCodes.Status429TooManyRequests,
                "You've reached today's limit for the assistant. It resets tomorrow — everything the app can " +
                "answer on its own still works.");
        }
    }

    /// <summary>
    /// Input the server is willing to forward. ⚠️ It cannot verify that the question was <em>masked properly</em> —
    /// only the client holds the vocabulary to know that — so this checks the things it can: length, slot count,
    /// slot kinds, and that every placeholder in the text has a slot behind it. Anything else is
    /// <c>unknown</c> without a call, which also makes a probing client cheap to serve.
    /// </summary>
    private bool IsWellFormed(AssistantAskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Question)) return false;
        if (req.Question.Length > MaxQuestionLength) return false;
        if (req.Slots.Count > MaxSlots) return false;

        foreach (var kind in req.Slots)
            if (kind is not (AssistantSlotKinds.Goal or AssistantSlotKinds.Category
                          or AssistantSlotKinds.Wallet or AssistantSlotKinds.Trip)) return false;

        foreach (Match m in PlaceholderPattern().Matches(req.Question))
            if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > req.Slots.Count) return false;

        return true;
    }

    /// <summary>
    /// The model's answer, checked against the catalogue it was given. Four ways to fail, all ending in
    /// <c>unknown</c>: an intent that is not one of the four, a key that does not exist, a key from the wrong
    /// catalogue for the intent, and a slot that is missing, out of range, or the wrong kind for the target.
    /// </summary>
    private static AssistantReplyDto Validate(AssistantReplyDto? reply, AssistantAskRequest req)
    {
        if (reply is null) return UnknownReply;

        switch (reply.Intent)
        {
            case AssistantIntents.Explain:
                return AssistantCatalogue.IsExplainer(reply.Target) ? reply with { Slot = null } : UnknownReply;

            case AssistantIntents.Report:
                return AssistantCatalogue.IsTopic(reply.Target) ? reply with { Slot = null } : UnknownReply;

            case AssistantIntents.Navigate:
                if (!AssistantCatalogue.IsTarget(reply.Target)) return UnknownReply;
                var needs = AssistantCatalogue.SlotKindFor(reply.Target!);
                if (needs is null) return reply with { Slot = null };
                // A target that needs an entity is useless without one, and actively wrong with the wrong one:
                // "open my {1}" where {1} is a wallet must not open a goal screen with a wallet's id.
                if (reply.Slot is not { } slot || slot < 1 || slot > req.Slots.Count) return UnknownReply;
                return req.Slots[slot - 1] == needs ? reply : UnknownReply;

            default:
                return UnknownReply;
        }
    }

    private static string CacheKey(AssistantAskRequest req) =>
        $"{string.Join(',', req.Slots)}|{req.Question.Trim().ToLowerInvariant()}";

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
