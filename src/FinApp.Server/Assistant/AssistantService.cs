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
public sealed partial class AssistantService(IAssistantParser parser, ILogger<AssistantService> log)
{
    /// <summary>Long enough for a real spoken question, short enough that this endpoint is not a way to push text
    /// through someone else's API key.</summary>
    public const int MaxQuestionLength = 240;

    /// <summary>More placeholders than this means the masker matched half the sentence, which is not a question.</summary>
    public const int MaxSlots = 8;

    /// <summary>
    /// Asks per user per day. This is a <b>cost</b> guard, not a security control, and it is deliberately
    /// per-instance: Cloud Run may run several, so the real ceiling is this times the instance count. A shared
    /// counter would mean a database round-trip on every ask to protect against a spend that is already bounded by
    /// the rate limiter. Revisit it when there is a bill to look at, not before.
    /// </summary>
    public const int DailyCap = 60;

    private static readonly AssistantReplyDto UnknownReply = new(AssistantIntents.Unknown, null, null);

    private readonly ConcurrentDictionary<(Guid User, DateOnly Day), int> _calls = new();

    // Keyed per user, deliberately. A masked question carries nothing personal — but the masker cannot mask a word
    // it does not recognise, so an unmatched noun ("at the corner shop") does travel as text. Caching that across
    // users would make one person's typing visible in another person's latency. Per-user, it is only ever their own.
    private readonly ConcurrentDictionary<(Guid User, string Key), AssistantReplyDto> _cache = new();

    public bool Available => parser.Available;

    public async Task<AssistantReplyDto> AskAsync(Guid userId, AssistantAskRequest req, CancellationToken ct = default)
    {
        if (!parser.Available)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "The assistant is not available right now.");

        if (!IsWellFormed(req)) return UnknownReply;

        var key = CacheKey(req);
        if (_cache.TryGetValue((userId, key), out var cached)) return cached;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var used = _calls.AddOrUpdate((userId, today), 1, (_, n) => n + 1);
        if (used > DailyCap)
        {
            log.LogInformation("Assistant: user hit the daily cap of {Cap}.", DailyCap);
            throw new ApiException(StatusCodes.Status429TooManyRequests,
                "You've reached today's limit for the assistant. It resets tomorrow.");
        }

        var reply = Validate(await parser.ParseAsync(req, ct), req);
        if (_cache.Count < 5_000) _cache[(userId, key)] = reply;
        return reply;
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
