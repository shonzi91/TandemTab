using System.Text;
using System.Text.Json;
using FinApp.Contracts;

namespace FinApp.Server.Assistant;

/// <summary>
/// The model's instructions and its output schema, both <b>generated from
/// <see cref="AssistantCatalogue"/></b> rather than written out by hand.
/// <para>
/// ★ That generation is the point, not a tidiness preference. A prompt that lists destinations and a client that
/// switches over destinations are two copies of one list, and the failure when they drift is silent: the model
/// confidently returns a key nothing handles, and the user gets "I didn't understand that" for a question the app
/// can perfectly well answer. Here the list exists once, in Contracts, where both sides read it.
/// </para>
/// </summary>
public static class AssistantPrompt
{
    /// <summary>Built once. It is also the cached prefix on every request — it is stable by construction, so the
    /// only thing that varies per call is the masked question itself.</summary>
    public static readonly string System = Build();

    private static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You route questions inside TandemTab, a personal budgeting app. You are a classifier, not a writer.");
        sb.AppendLine();
        sb.AppendLine("The question you are given is MASKED: the app has already recognised the names of the user's own");
        sb.AppendLine("categories, goals, wallets and journeys and replaced each one with a numbered placeholder like {1}.");
        sb.AppendLine("You are told what kind of thing each placeholder is, never what it is called. Treat a placeholder as an");
        sb.AppendLine("opaque token — do not guess at its name, and do not ask for it.");
        sb.AppendLine();
        sb.AppendLine("Choose exactly one intent:");
        sb.AppendLine("  navigate — the user wants to get to a screen. Answer with a target key.");
        sb.AppendLine("  explain  — the user is asking how something works. Answer with an explainer key.");
        sb.AppendLine("  report   — the user is asking about their own figures. Answer with a topic key.");
        sb.AppendLine("  unknown  — anything else, including anything you are not confident about.");
        sb.AppendLine();
        sb.AppendLine("RULES, in order of importance:");
        sb.AppendLine("1. You never state a number, an amount, a date, a balance or any fact about the user's money.");
        sb.AppendLine("   You do not answer the question. The app computes every figure itself and writes every sentence");
        sb.AppendLine("   the user reads. Your entire output is the keys below.");
        sb.AppendLine("2. Prefer 'unknown' over a plausible guess. A wrong screen wastes a tap; a confident wrong answer");
        sb.AppendLine("   about someone's money is the thing this design exists to prevent.");
        sb.AppendLine("3. Use a key exactly as written below. Never invent one.");
        sb.AppendLine("4. If a target needs a placeholder of a given kind and the question has no placeholder of that kind,");
        sb.AppendLine("   the answer is the matching general screen, or 'unknown'. Do not attach a placeholder of the wrong kind.");
        sb.AppendLine("5. Questions asking to change, add, delete or move anything are NOT writes you can perform. If there is");
        sb.AppendLine("   a form for it, navigate to that form; the user fills it in themselves.");
        sb.AppendLine("6. The question may be in any language. The keys are always English.");
        sb.AppendLine();
        Section(sb, "TARGET KEYS (intent = navigate)", AssistantCatalogue.Targets);
        Section(sb, "EXPLAINER KEYS (intent = explain)", AssistantCatalogue.Explainers);
        Section(sb, "TOPIC KEYS (intent = report)", AssistantCatalogue.Topics);
        sb.AppendLine("Fields you do not need: send target as an empty string and slot as 0.");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<AssistantOption> options)
    {
        sb.AppendLine(title);
        foreach (var o in options)
        {
            var slot = o.NeedsSlot is not null
                ? $" (requires a placeholder of kind '{o.NeedsSlot}'; put its number in slot)"
                : o.TakesSlot is not null
                    ? $" (if the question names a placeholder of kind '{o.TakesSlot}', put its number in slot; " +
                      "otherwise send slot as 0 and it answers for the whole account)"
                    : "";
            sb.AppendLine($"  {o.Key} — {o.What}{slot}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// The output schema. ⚠️ <b>Every field is required and none is nullable</b> — an unused target is the empty
    /// string and an unused slot is 0. Optional-and-nullable is the shape that invites a model to omit a field and
    /// a deserializer to disagree about what that meant; "always present, sometimes empty" has one reading.
    /// </summary>
    public static Dictionary<string, JsonElement> Schema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            intent = new
            {
                type = "string",
                @enum = new[]
                {
                    AssistantIntents.Navigate, AssistantIntents.Explain,
                    AssistantIntents.Report, AssistantIntents.Unknown,
                },
                description = "Which of the four kinds of question this is.",
            },
            target = new
            {
                type = "string",
                description = "The target, explainer or topic key. Empty string when the intent is unknown.",
            },
            slot = new
            {
                type = "integer",
                description = "The placeholder number this refers to, or 0 when the answer needs none.",
            },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "intent", "target", "slot" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };
}
