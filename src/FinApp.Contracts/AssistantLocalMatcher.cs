using System.Text.RegularExpressions;

namespace FinApp.Contracts;

/// <summary>
/// Answers a question <b>without a model</b> when it plainly can, and returns null when it cannot.
/// <para>
/// ★ <b>Why this exists in front of the API call, not instead of it.</b> The catalogue is 39 fixed keys and most
/// real questions are variations on a handful of them, so the common case is a lookup, not an inference. Every
/// question this resolves costs nothing, adds no latency, and — because it runs on the client beside the masker —
/// <b>never leaves the device at all</b>, which is a stronger privacy position than masking. What falls through
/// goes to the model exactly as before.
/// </para>
/// <para>
/// ★ <b>The failure mode is the reason this is safe.</b> A brittle keyword rule that stops matching costs a cent
/// and a round-trip, not a wrong answer — the model catches it. That is the opposite of the usual keyword-matcher
/// bargain, and it is what makes first-match-wins ordering acceptable here rather than a scoring model.
/// </para>
/// <para>
/// ⚠️ <b>Bulgarian is partial, on purpose.</b> The terms below are lifted from the app's own translations rather
/// than invented, and they are matched as <b>stems</b> because the language inflects heavily ("бюджет" catches
/// "бюджета", "бюджети"). Coverage is thinner than English; the shortfall is a cost, not a failure, because those
/// questions fall through to a model that handles any language.
/// </para>
/// </summary>
public static partial class AssistantLocalMatcher
{
    /// <summary>The answer, or null to fall through to the model.</summary>
    public static AssistantReplyDto? Match(string maskedQuestion, IReadOnlyList<string> slotKinds)
    {
        if (string.IsNullOrWhiteSpace(maskedQuestion)) return null;
        var q = " " + maskedQuestion.Trim().ToLowerInvariant() + " ";

        var kind = FirstSlot(maskedQuestion, slotKinds);
        var kls = Classify(q);

        // An entity in the question beats any generic answer — "how much have I spent on {1}" is a question about
        // {1}, and answering it with the period's whole total would be answering a different question. The one
        // exception is an explainer: "how do {1} goals work" is about the feature, not that row.
        if (kind is { } slot && kls != Class.Explain)
        {
            var target = slot.Kind switch
            {
                AssistantSlotKinds.Goal => "open.goal",
                AssistantSlotKinds.Category => "open.category",
                AssistantSlotKinds.Wallet => "open.wallet",
                AssistantSlotKinds.Trip => "open.trip",
                _ => null,
            };
            if (target is not null) return Checked(new AssistantReplyDto(AssistantIntents.Navigate, target, slot.Index));
        }

        var rules = kls switch
        {
            Class.Report => ReportRules,
            Class.Explain => ExplainRules,
            _ => NavigateRules,   // also the fallback for a bare "goals", which has no question word at all
        };

        foreach (var (target, phrases) in rules)
            if (phrases.Any(p => q.Contains(p, StringComparison.Ordinal)))
                return Checked(new AssistantReplyDto(IntentFor(kls), target, null));

        return null;
    }

    /// <summary>
    /// The same validation the server applies to a model's answer, applied to our own. ★ A key here that the
    /// catalogue does not carry is a typo nothing else would catch — the client's switch would fall to its default
    /// and the user would get "I didn't follow that" for a question that matched perfectly. Falling through to the
    /// model instead makes the worst case a cost, which is this whole file's bargain.
    /// </summary>
    private static AssistantReplyDto? Checked(AssistantReplyDto reply) => reply.Intent switch
    {
        AssistantIntents.Navigate when AssistantCatalogue.IsTarget(reply.Target) => reply,
        AssistantIntents.Explain when AssistantCatalogue.IsExplainer(reply.Target) => reply,
        AssistantIntents.Report when AssistantCatalogue.IsTopic(reply.Target) => reply,
        _ => null,
    };

    private enum Class { Report, Explain, Navigate }

    private static string IntentFor(Class c) => c switch
    {
        Class.Report => AssistantIntents.Report,
        Class.Explain => AssistantIntents.Explain,
        _ => AssistantIntents.Navigate,
    };

    /// <summary>
    /// What kind of question this is. ⚠️ <b>Report is tested before explain, and the order is load-bearing:</b>
    /// "what is my safe to spend" and "what is safe to spend" differ by one word, and only the first is a question
    /// about the user's own figures. Note also that "how is" is deliberately absent from the explain list — "how
    /// is my {1} doing" is not a request for documentation.
    /// </summary>
    private static Class Classify(string q)
    {
        if (Report.Any(p => q.Contains(p, StringComparison.Ordinal))) return Class.Report;
        if (Explain.Any(p => q.Contains(p, StringComparison.Ordinal))) return Class.Explain;
        return Class.Navigate;
    }

    private static readonly string[] Report =
    [
        "how much", "how many", "what's my", "whats my", "what is my", "am i", "do i have",
        "have i spent", "can i spend", "what did i", "колко", "имам ли",
        // "which budgets are over" reads as navigation by every other test here, and it is not: the answer is a
        // sentence naming them, and sending someone to the list to count for themselves is a worse one.
        "which budget", "over budget", "anything over",
    ];

    private static readonly string[] Explain =
    [
        "how does", "how do", "how da", "what is", "what are", "what does", "what happens", "why",
        "explain", "means", "mean by", "как работи", "какво е", "какво озна", "защо",
    ];

    private static (string Kind, int Index)? FirstSlot(string question, IReadOnlyList<string> slotKinds)
    {
        var m = PlaceholderPattern().Match(question);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        return n >= 1 && n <= slotKinds.Count ? (slotKinds[n - 1], n) : null;
    }

    // ── The rule tables ───────────────────────────────────────────────────────────────────────────────────────
    // First match wins, so each table is ordered most-specific first. A phrase that is a substring of another
    // ("spend" vs "safe to spend") must therefore come after it.

    private static readonly (string Target, string[] Phrases)[] ReportRules =
    [
        ("report.safeToSpend", ["safe to spend", "can i spend", "left to spend", "свободно", "харчене"]),
        ("report.budgets",     ["over budget", "budgets over", "budget", "бюджет"]),
        ("report.topCategory", ["most", "top category", "biggest", "largest", "най"]),
        ("report.saved",       ["set aside", "saved", "saving", "спест", "задел"]),
        ("report.spent",       ["spent", "spend", "gone", "разход", "похарч"]),
    ];

    private static readonly (string Target, string[] Phrases)[] ExplainRules =
    [
        ("explain.safeToSpend", ["safe to spend", "свободно за харчене"]),
        ("explain.runway",      ["runway"]),
        ("explain.healthScore", ["health score", "оценка на здравето"]),
        ("explain.debtFree",    ["debt free", "debt-free", "payoff", "pay off", "paid off"]),
        ("explain.privacy",     ["privacy", "private", "send", "assistant", "my data", "поверителн"]),
        ("explain.budgets",     ["budget", "бюджет"]),
        ("explain.savings",     ["set aside", "savings", "saving", "goal", "debt", "спест", "задел", "цел"]),
        ("explain.periods",     ["period", "next month", "период"]),
        ("explain.trips",       ["trip", "journey", "пътуван"]),
        ("explain.sharing",     ["share", "sharing", "partner", "invite", "споделя"]),
        ("explain.bank",        ["bank", "банк"]),
    ];

    private static readonly (string Target, string[] Phrases)[] NavigateRules =
    [
        ("open.weekRecap",    ["week", "recap", "седмиц"]),
        ("open.achievements", ["achievement", "medal", "milestone", "trophy", "постижен"]),
        ("open.healthScore",  ["health score", "оценка на здравето"]),
        ("open.runwayMath",   ["runway"]),
        ("open.breakdown",    ["breakdown", "where my money went", "where the money went", "where did my money"]),
        ("open.trends",       ["trend", "over time", "тенденц"]),
        ("open.recurring",    ["bill", "recurring", "subscription", "повтарящ"]),
        ("open.import",       ["import", "statement", "внос"]),
        ("open.bankReview",   ["review", "pending", "за преглед"]),
        ("open.archived",     ["archive", "архив"]),
        ("open.categories",   ["categor", "категор"]),
        ("open.tags",         ["tag", "етикет"]),
        ("open.invite",       ["invite", "share account", "покан"]),
        ("open.nextPeriod",   ["next period", "start next", "new month", "следващ период"]),
        ("open.budgets",      ["budget", "бюджет"]),
        ("tab.goals",         ["goal", "debt", "цел", "дълг"]),
        ("tab.spending",      ["spending", "expenses", "expense", "разход"]),
        ("tab.wallets",       ["wallet", "bank", "balance", "портфейл", "банк"]),
        ("tab.dashboard",     ["dashboard", "home", "overview", "табло"]),
    ];

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
