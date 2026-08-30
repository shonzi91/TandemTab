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
    /// <summary>
    /// ⭐ <b>Haiku, not Opus, and the reasoning is the cost of the job rather than the size of the model.</b> This
    /// call picks one of 39 keys from a fixed menu under a strict output schema, with a validation layer behind it
    /// that catches a wrong answer whoever produced it. Opus was ~5× the price for a classification, and by the
    /// time a question reaches here the local matcher has already taken the easy ones — so what arrives is
    /// unusual phrasing, which is a vocabulary problem, not a reasoning one.
    /// </summary>
    private const string DefaultModel = "claude-haiku-4-5";

    private readonly AnthropicClient? _client;
    private readonly ILogger<AnthropicAssistantParser> _log;
    private readonly string _model;

    public AnthropicAssistantParser(IConfiguration config, ILogger<AnthropicAssistantParser> log)
    {
        _log = log;
        _model = config["Anthropic:Model"] ?? DefaultModel;
        var key = config["Anthropic:ApiKey"];
        _client = string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient { ApiKey = key };
        if (_client is null)
            _log.LogInformation("Assistant: no Anthropic:ApiKey configured — the assistant is off.");
    }

    /// <summary>
    /// ⚠️⚠️ <b>Two request settings are coupled to the model, and getting either wrong fails silently.</b> This is
    /// why the model is a config value with a profile rather than a bare string somebody can swap.
    /// <list type="number">
    /// <item><b>Effort is rejected by Haiku.</b> <c>OutputConfig.Effort</c> is not accepted on Haiku 4.5 — sending
    /// it 400s, the parse "fails", and every question degrades to <c>unknown</c> while looking like a model that
    /// simply never understands anything.</item>
    /// <item><b>Thinking is on by default on Opus 5, and thinking tokens count against MaxTokens.</b> A 256-token
    /// budget that was ample for a three-field JSON answer is not ample once reasoning shares it: the reply
    /// truncates, no valid JSON comes back, and again every question becomes <c>unknown</c>. So the reasoning
    /// models get headroom and Haiku, which does not think here, does not need it.</item>
    /// </list>
    /// Both failures look identical from the outside — an assistant that never understands anything — which is
    /// exactly the kind of silent failure this app has been bitten by before.
    /// </summary>
    private bool IsHaiku => _model.StartsWith("claude-haiku", StringComparison.Ordinal);
    private int MaxTokens => IsHaiku ? 256 : 1024;

    public bool Available => _client is not null;

    public async Task<AssistantReplyDto?> ParseAsync(AssistantAskRequest req, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var slots = req.Slots.Count == 0
            ? "The question contains no placeholders."
            : "Placeholders: " + string.Join(", ", req.Slots.Select((kind, i) => $"{{{i + 1}}} is a {kind}"));

        try
        {
            // Effort is init-only, so the branch is on the whole object. See the note on MaxTokens: Haiku rejects
            // Effort outright, and a reasoning model needs it to keep this cheap.
            var format = new JsonOutputFormat { Schema = AssistantPrompt.Schema() };
            var output = IsHaiku
                ? new OutputConfig { Format = format }
                : new OutputConfig { Format = format, Effort = Effort.Low };

            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = MaxTokens,
                System = new List<TextBlockParam>
                {
                    // The prompt is generated from a static catalogue, so it is byte-stable across every request in
                    // a deployment. ⚠️ It is ~1,240 tokens, and the minimum cacheable prefix is ~1,024 — trimming
                    // the catalogue could drop it under the bar, at which point caching silently stops happening
                    // and the only symptom is a bigger bill.
                    new() { Text = AssistantPrompt.System, CacheControl = new CacheControlEphemeral() },
                },
                OutputConfig = output,
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
