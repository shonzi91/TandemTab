using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using FinApp.Contracts;

namespace FinApp.Server.Assistant;

/// <summary>
/// Turns a masked question into an intent. One method, one implementation per home for the model.
/// <para>
/// ★ This interface is the seam the roadmap's R9 swings on: the on-device assistant replaces this and nothing
/// else, because the masking that happens before it and the execution that happens after it both live on the
/// client. It is also what lets the whole API surface be tested without a network call.
/// </para>
/// </summary>
public interface IAssistantParser
{
    /// <summary>False when no key is configured. The endpoint answers 503 rather than pretending, and the client
    /// hides the assistant entirely — a control that always fails is worse than no control.</summary>
    bool Available { get; }

    /// <summary>The model's raw choice, or null when the call failed. ⚠️ The result is <b>untrusted</b>: validate
    /// it against the catalogue before it reaches a client.</summary>
    Task<AssistantReplyDto?> ParseAsync(AssistantAskRequest req, CancellationToken ct = default);
}

/// <summary>
/// The cloud implementation. ⚠️ <b>What crosses this boundary is the whole privacy argument</b>, so it is worth
/// being precise about it: the request carries the masked question, the list of placeholder kinds, and a prompt
/// generated from a static catalogue. It carries no account id, no user id, no figure, and no name the user wrote.
/// The client's CSP keeps <c>connect-src 'self'</c> for exactly this reason — the browser never talks to anyone
/// but this server.
/// </summary>
public sealed class AnthropicAssistantParser : IAssistantParser
{
    // A one-line classification against a fixed menu. Low effort is the honest setting for it: the work is
    // recognising which of ~40 keys a sentence means, not reasoning about anything.
    private const string ModelId = "claude-opus-5";
    private const int MaxTokens = 256;

    private readonly AnthropicClient? _client;
    private readonly ILogger<AnthropicAssistantParser> _log;

    public AnthropicAssistantParser(IConfiguration config, ILogger<AnthropicAssistantParser> log)
    {
        _log = log;
        var key = config["Anthropic:ApiKey"];
        _client = string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient { ApiKey = key };
        if (_client is null)
            _log.LogInformation("Assistant: no Anthropic:ApiKey configured — the assistant is off.");
    }

    public bool Available => _client is not null;

    public async Task<AssistantReplyDto?> ParseAsync(AssistantAskRequest req, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var slots = req.Slots.Count == 0
            ? "The question contains no placeholders."
            : "Placeholders: " + string.Join(", ", req.Slots.Select((kind, i) => $"{{{i + 1}}} is a {kind}"));

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = MaxTokens,
                System = new List<TextBlockParam>
                {
                    // The prompt is generated from a static catalogue, so it is byte-stable across every request
                    // in a deployment — which is what makes caching it worth a breakpoint at all.
                    new() { Text = AssistantPrompt.System, CacheControl = new CacheControlEphemeral() },
                },
                OutputConfig = new OutputConfig
                {
                    Effort = Effort.Low,
                    Format = new JsonOutputFormat { Schema = AssistantPrompt.Schema() },
                },
                Messages = [new() { Role = Role.User, Content = $"{slots}\n\nQuestion: {req.Question}" }],
            }, cancellationToken: ct);

            var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(json)) return null;

            var parsed = JsonSerializer.Deserialize<Raw>(json);
            if (parsed is null) return null;

            return new AssistantReplyDto(
                parsed.intent ?? AssistantIntents.Unknown,
                string.IsNullOrWhiteSpace(parsed.target) ? null : parsed.target,
                parsed.slot > 0 ? parsed.slot : null);
        }
        catch (Exception ex)
        {
            // ⚠️ Logged with the SHAPE of the question and never the question — the same rule the wire follows.
            // A failed ask degrades to suggestion chips; it does not take a screen down.
            _log.LogWarning(ex, "Assistant: the parse call failed ({Length} chars, {Slots} slots).",
                req.Question.Length, req.Slots.Count);
            return null;
        }
    }

    /// <summary>Deserialization target for the model's JSON. Lower-case names match the schema exactly, so nothing
    /// depends on a serializer's naming policy.</summary>
    private sealed record Raw(string? intent, string? target, int slot);
}
