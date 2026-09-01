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

        // ⭐ Comparison is tested FIRST, ahead of both the entity rule and the classes, and the order is the whole
        // point. "why did my {1} jump" is an Explain-class question by its words and an entity question by its
        // placeholder, and it is neither: it is a question about change. Left to the rules below, the "why" sent it
        // to the explainers, which have nothing to say about it, and it came back "I didn't follow that one" — the
        // single most common failure this matcher had.
        if (Comparison.Any(p => q.Contains(p, StringComparison.Ordinal)))
        {
            // A category sharpens it; anything else is ignored rather than refused, since the topic answers
            // account-wide perfectly well on its own.
            var slotNo = kind is { Kind: AssistantSlotKinds.Category, Index: var i } ? i : (int?)null;
            return Checked(new AssistantReplyDto(AssistantIntents.Report, "report.compare", slotNo));
        }

        // An entity in the question beats any generic answer — "how much have I spent on {1}" is a question about
        // {1}, and answering it with the period's whole total would be answering a different question. The one
        // exception is an explainer: "how do {1} goals work" is about the feature, not that row.
        if (kind is { } slot && kls != Class.Explain && EntityTarget(slot.Kind) is { } entity)
            return Checked(new AssistantReplyDto(AssistantIntents.Navigate, entity, slot.Index));

        var rules = kls switch
        {
            Class.Report => ReportRules,
            Class.Explain => ExplainRules,
            _ => NavigateRules,   // also the fallback for a bare "goals", which has no question word at all
        };

        foreach (var (target, phrases) in rules)
            if (phrases.Any(p => q.Contains(p, StringComparison.Ordinal)))
                return Checked(new AssistantReplyDto(IntentFor(kls), target, null));

        // ⭐⭐ The entity rule, a second time, for the Explain class only — and the ORDER is what makes it correct.
        // The guard above skips an entity when the question reads as documentation, because "how do {1} goals
        // work" is about the feature and not that row. But it was skipping the entity for EVERY question with an
        // explain-ish opener, and most of those are not documentation questions at all: "what is in my {wallet}",
        // "why is my {goal} behind", "what does my {goal} cost" all named a thing the app can open, and all
        // reached the model instead.
        // Running it here rather than up there is the whole fix: a real explainer question matches the table above
        // and never gets this far ("how do {1} goals work" contains "goal" → explain.savings), so the intent is
        // preserved. What arrives here is a question that started with an explain word, matched no explainer, and
        // names an entity — which is an entity question wearing a hat.
        // ⚠️⚠️ Except when the question carries a documentation VERB, and that exception is not a nicety — it is
        // An_explainer_is_not_hijacked_by_a_thing_that_happens_to_be_named, which this fallback failed on the
        // first attempt. A goal named "budgets" turns "how do budgets work" into "how do {1} work": the word that
        // said WHICH explainer was wanted has been masked away, and the honest answer is to decline and let the
        // model see it. Opening the row is the one thing that must never happen — the user asked how a feature
        // works and would be shown a savings drawer.
        // So the cut is: is the entity the SUBJECT BEING EXPLAINED ("how do {1} work") or the OBJECT of an
        // ordinary question ("what is in my {1}")? A documentation verb is what tells them apart.
        // ⚠️ This can only ADD matches. Everything reaching this line was returning null and costing a model call.
        if (kind is { } late && kls == Class.Explain
            && !Documentation.Any(p => q.Contains(p, StringComparison.Ordinal))
            && EntityTarget(late.Kind) is { } lateTarget)
            return Checked(new AssistantReplyDto(AssistantIntents.Navigate, lateTarget, late.Index));

        return null;
    }

    /// <summary>
    /// Words that make a question documentation about a thing rather than a question about it — the difference
    /// between "how does my {1} <b>work</b>" and "what is in my {1}".
    /// <para>⚠️ Deliberately tiny, and only consulted by Match's late entity fallback. It is not a class test: a
    /// question with one of these still reaches the explainer table normally. All it does is stop an entity being
    /// opened when the entity is the subject the question wanted explained — and masking may already have removed
    /// the word that said which explainer that was, in which case declining is the right answer.</para>
    /// </summary>
    private static readonly string[] Documentation =
        ["work", "mean", "explain", "работи", "означава", "обясни"];

    /// <summary>The screen that opens one named thing, or null for a slot kind with no screen of its own.</summary>
    private static string? EntityTarget(string slotKind) => slotKind switch
    {
        AssistantSlotKinds.Goal => "open.goal",
        AssistantSlotKinds.Category => "open.category",
        AssistantSlotKinds.Wallet => "open.wallet",
        AssistantSlotKinds.Trip => "open.trip",
        _ => null,
    };

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
        // ⚠️ "when will my loans be paid off" is a question about MY figures, but none of the words above appear
        // in it — it was landing in the navigate class and being answered with a screen. The class list is what
        // decides which rule table runs, so a phrase added only to the rules below never gets reached.
        "how long", "when will", "when do", "when does", "when am", "what bills", "still due", "coming out",
        "докога", "кога ще",
        // "which budgets are over" reads as navigation by every other test here, and it is not: the answer is a
        // sentence naming them, and sending someone to the list to count for themselves is a worse one.
        "which budget", "over budget", "anything over",
    ];

    private static readonly string[] Explain =
    [
        "how does", "how do", "what is", "what are", "what does", "what happens", "why",
        "explain", "means", "mean by", "как работи", "какво е", "какво озна", "защо",
        // ⚠️ These two were missing while Report carried all three forms of "what's my", and the gap between the
        // lists was one sentence: "what's safe to spend" has no "my" so Report declines it, and Explain knew only
        // the uncontracted "what is" — so it fell to Navigate, matched nothing, and cost a model call.
        // ⚠️⚠️ Adding them was tried ONCE BEFORE and reverted, because an entity used to be discarded outright for
        // the Explain class and "what's in my {wallet}" stopped resolving. That only worked by accident — the
        // contraction's absence here was what kept it in the Navigate class. Match's late entity fallback is what
        // makes this safe, so do not remove that and leave these.
        // Verify any change with `dotnet run --project tools/FinApp.AssistantProbe -- --misses`; the log cannot
        // see any of this, which is why the tool exists.
        "what's", "whats",
    ];

    /// <summary>Words that make a question about <b>change</b> rather than a current figure. ⚠️ Kept narrow on
    /// purpose: "more" and "less" alone appear in plenty of questions that are not comparisons, and a false hit
    /// here answers the wrong question confidently rather than falling through to the model.</summary>
    private static readonly string[] Comparison =
    [
        "last month", "last period", "previous month", "previous period", "than last", "than before",
        "compare", "compared", "comparison", "vs ", "versus",
        "jump", "spike", "increase", "decrease",
        "went up", "gone up", "going up", "go up", "goes up",
        "went down", "gone down", "going down", "go down", "goes down",
        "rise", "risen", "rose", "fell", "fallen", "higher", "lower", "more than usual", "changed",
        "миналия месец", "минал месец", "сравн", "спрямо", "вдигна", "покачи", "увеличи", "намаля", "скочи",
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
        // ⚠️ These three come FIRST because they are the specific questions. "how long will my money last" also
        // contains nothing that would match below it, but "when am I debt free" contains "debt", which would
        // otherwise be swallowed by the savings rule and answered with a set-aside total.
        ("report.runway",      ["last", "run out", "runway", "how long", "стигнат", "докога"]),
        // ⚠️ The Bulgarian half was one phrase deep ("без дълг" = "debt free") while the English half had six,
        // so "кога ще изплатя заемите" — the commonest way to ask this in Bulgarian — reached the right CLASS
        // ("кога ще" is in Report) and then matched no rule. The verbs are what people actually type: изплатя /
        // изплащам (pay off), погасявам (repay). Stems only, because Bulgarian conjugates.
        ("report.debtFree",    ["debt free", "debt-free", "paid off", "pay off", "payoff", "clear my debt",
                                "cleared", "без дълг", "изплат", "погас"]),
        ("report.bills",       ["bill", "due", "coming out", "recurring", "subscription", "сметк", "падеж"]),
        // ⚠️ "have left" earns its place and bare "left" would not: "left to spend" was already here, but "how
        // much money do I have left" — the same question without the app's own words — matched nothing and paid
        // for a model call. Bare "left" would swallow "how much is left in my {goal}", which is a savings
        // question, so the pronoun is doing the work and has to stay in the phrase.
        ("report.safeToSpend", ["safe to spend", "can i spend", "left to spend", "i have left", "do i have left",
                                "свободно", "харчене"]),
        ("report.budgets",     ["over budget", "budgets over", "budget", "бюджет"]),
        ("report.topCategory", ["most", "top category", "biggest", "largest", "най"]),
        // "put aside" is the ordinary English for this and the app's own word is "set aside" — the table knew
        // only its own vocabulary, which is the shape of most of these misses.
        ("report.saved",       ["set aside", "put aside", "saved", "saving", "спест", "задел"]),
        ("report.spent",       ["spent", "spend", "gone", "разход", "похарч"]),
    ];

    private static readonly (string Target, string[] Phrases)[] ExplainRules =
    [
        // ⚠️ The Bulgarian entry knew only the app's own label ("свободно за харчене"). "какво е безопасно да
        // похарча" is how the question is actually asked, and it lands here rather than in Report for exactly the
        // reason the English twin does: "какво е" is an explain opener, as "what's" is. Asking it WITH a
        // possessive ("какво е моето свободно…") still reaches Report first, which is the intended split.
        ("explain.safeToSpend", ["safe to spend", "свободно за харчене", "безопасно да похарч", "безопасно за харч"]),
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
        // ⚠️ "achievement" is a noun and people use the verb: "what have I achieved" matched nothing, because
        // "achieved" does not contain "achievement". The stem catches achieve / achieved / achievements without
        // widening it to anything else — the same trap as "put aside" above, one letter further in.
        ("open.achievements", ["achieve", "medal", "milestone", "trophy", "постижен"]),
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
        // "loan" and "mortgage" sit here because the app's word is "debt" and nobody else's is. "is it worth
        // overpaying the loan" answered "I didn't follow that one" against an app with a whole payoff planner.
        ("tab.goals",         ["goal", "debt", "loan", "mortgage", "payoff", "цел", "дълг", "заем", "кредит"]),
        ("tab.spending",      ["spending", "expenses", "expense", "разход"]),
        ("tab.wallets",       ["wallet", "bank", "balance", "портфейл", "банк"]),
        ("tab.dashboard",     ["dashboard", "home", "overview", "табло"]),
    ];

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
