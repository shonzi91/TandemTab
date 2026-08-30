namespace FinApp.Contracts;

/// <summary>
/// The assistant's wire contract (roadmap R3 — "narrate, don't compute").
/// <para>
/// ⚠️ <b>Read the shape of <see cref="AssistantAskRequest"/> before changing anything here.</b> The question that
/// crosses this boundary is <b>masked</b>: the client resolves the user's own vocabulary — category, goal, wallet
/// and journey names — against the account it already holds, and replaces every span it recognised with a numbered
/// placeholder. "how is my car fund doing" leaves as <c>how is my {1} doing</c> with <c>Slots = ["goal"]</c>.
/// </para>
/// <para>
/// That is the whole privacy design in one sentence: <b>the model is told the shape of the question, never its
/// contents</b>, and it answers with an intent, never with a figure. Every number the user sees still comes from
/// the deterministic engine and is rendered through the same localized message codes the health insights use
/// (<see cref="InsightMessageDto"/>) — so there is no path by which a model can state an amount.
/// </para>
/// </summary>
public sealed record AssistantAskRequest(
    /// <summary>The masked question. Placeholders are <c>{1}</c>, <c>{2}</c>… indexed from one.</summary>
    string Question,
    /// <summary>What each placeholder is, in order — one of <see cref="AssistantSlotKinds"/>. The model needs the
    /// <em>kind</em> to route ("open my {1}" means a different screen for a goal than for a wallet); it does not
    /// need, and never receives, the name.</summary>
    IReadOnlyList<string> Slots,
    /// <summary>How many questions <see cref="AssistantLocalMatcher"/> answered on the device since the last time
    /// one reached here.
    /// <para>★ It rides a request that was happening anyway, so measuring the local hit rate costs no endpoint, no
    /// extra call and no new privacy surface — it is a count, never a question. ⚠️ It is also structurally
    /// pessimistic: a session where the matcher answers <em>everything</em> never sends a request, so its perfect
    /// score is never reported. That is the right bias — the case it under-counts is the case with no bill.</para>
    /// </summary>
    int LocalHits = 0);

/// <summary>
/// What the model decided the question was. <see cref="Intent"/> is one of <see cref="AssistantIntents"/>;
/// <see cref="Target"/> is a key from the matching catalogue below; <see cref="Slot"/> is a one-based index into
/// the request's slots when the target needs an entity.
/// <para>⚠️ Everything here is <b>untrusted</b> — a model may return a key that does not exist, or a slot index
/// that is out of range. The server validates against the catalogue and downgrades anything it cannot recognise to
/// <see cref="AssistantIntents.Unknown"/>, which is a real answer (suggestion chips), not an error.</para>
/// </summary>
public sealed record AssistantReplyDto(string Intent, string? Target, int? Slot);

/// <summary>Whether this deployment can run the assistant at all, and what is left of this month's budget.
/// <para>A build with no key configured says so once and the client hides the control — a button that always
/// fails is worse than no button. <see cref="MonthlyRemaining"/> counts only questions that reach the model;
/// everything answered on the device is free and unlimited, which is why the number can sit still all day.</para>
/// </summary>
public sealed record AssistantStatusDto(bool Available, int MonthlyRemaining = 0, int MonthlyCap = 0);

public static class AssistantIntents
{
    /// <summary>Go to a screen the app already has. The back gesture undoes it, which is why navigation is the
    /// safe intent to ship first: a wrong one costs a tap.</summary>
    public const string Navigate = "navigate";
    /// <summary>Answer "how does X work" from the app's own static, translated copy.</summary>
    public const string Explain = "explain";
    /// <summary>Narrate a figure the engine already computed. The model picks the <em>topic</em>; the client reads
    /// the number.</summary>
    public const string Report = "report";
    /// <summary>Not understood, or understood as something out of scope. Answered with suggestion chips —
    /// <b>never</b> with a guess.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The kinds of thing a masked placeholder can stand for. Deliberately four: they are the named entities
/// a person refers to out loud, and each one has a screen to open.</summary>
public static class AssistantSlotKinds
{
    public const string Goal = "goal";          // a savings bucket, debt or investment
    public const string Category = "category";  // a budget category
    public const string Wallet = "wallet";      // a fund
    public const string Trip = "trip";          // a journey
}

/// <summary>One destination, explainer or topic the assistant may choose, with the one-line description that goes
/// into the model's prompt. <see cref="NeedsSlot"/> marks the targets that are meaningless without an entity.</summary>
public sealed record AssistantOption(string Key, string What, string? NeedsSlot = null);

/// <summary>
/// The closed set of things the assistant can do. <b>The prompt is generated from this list</b>, so a key can
/// never be advertised to the model without existing here, and the client's switch is exhaustive over the same
/// list. Adding a destination is one row plus one case.
/// </summary>
public static class AssistantCatalogue
{
    /// <summary>Screens. Every one of these already exists in the app — "navigation" is picking from a menu, not
    /// building anything.</summary>
    public static readonly IReadOnlyList<AssistantOption> Targets =
    [
        new("tab.dashboard",   "the Dashboard tab: the money hero, alerts, runway and milestones"),
        new("tab.spending",    "the Spending tab: budgets, the expense list and the category breakdown"),
        new("tab.goals",       "the Goals tab: savings goals, debts and investments"),
        new("tab.wallets",     "the Wallets tab: wallet balances, income for the period and the bank connection"),

        new("open.goal",       "one particular savings goal or debt",            AssistantSlotKinds.Goal),
        new("open.category",   "one particular budget category and its expenses", AssistantSlotKinds.Category),
        new("open.wallet",     "one particular wallet and its movements",         AssistantSlotKinds.Wallet),
        new("open.trip",       "one particular journey",                          AssistantSlotKinds.Trip),

        new("open.breakdown",  "the chart of where this period's money went, by category"),
        new("open.trends",     "the chart of money in, spent and set aside over past periods"),
        new("open.weekRecap",  "the recap of the week just gone"),
        new("open.achievements", "milestones and achievements earned"),
        new("open.healthScore", "the health score and the insights behind it"),
        new("open.runwayMath", "the working behind the runway figure"),
        new("open.recurring",  "recurring bills and income"),
        new("open.bankReview", "bank transactions waiting to be reviewed"),
        new("open.import",     "importing a bank statement file"),
        new("open.archived",   "archived items"),
        new("open.categories", "managing the category list"),
        new("open.tags",       "managing tags"),
        new("open.budgets",    "the list of budgets, where a budget's amount is changed"),
        new("open.invite",     "sharing this account with someone"),
        new("open.nextPeriod", "starting the next period"),
    ];

    // ⚠️ Deliberately absent: the add-expense, income, transfer and new-goal FORMS. Each is opened today by a
    // method that seeds its draft first, and a form reached without that seeding is a form that misbehaves in
    // ways only a person driving it would notice. They are worth adding — one at a time, each verified on a
    // running app — and not worth adding as a block of nine untested cases in the session that built the feature.

    /// <summary>"How does X work" answers. Static, translated copy that the app already has to have — the model
    /// selects one, and writes none of it.</summary>
    public static readonly IReadOnlyList<AssistantOption> Explainers =
    [
        new("explain.runway",        "what the runway figure means and how it is worked out"),
        new("explain.safeToSpend",   "what safe-to-spend means, and what 'after bills' takes off it"),
        new("explain.healthScore",   "what the health score is made of"),
        new("explain.budgets",       "how budgets work: advisory plans, not reserved cash"),
        new("explain.savings",       "how savings goals, debts and set-aside money work"),
        new("explain.debtFree",      "how the debt-free date is projected, and why the plan's date differs from the do-nothing one"),
        new("explain.periods",       "how periods work, and what starting the next one does"),
        new("explain.trips",         "how journeys work and what booked-ahead spending means"),
        new("explain.sharing",       "how sharing an account with someone works"),
        new("explain.privacy",       "privacy mode, and what this assistant does and does not send"),
        new("explain.bank",          "how the bank connection works and when it checks for new transactions"),
    ];

    /// <summary>Questions about the user's own figures. ⚠️ The model chooses the topic and <b>nothing else</b>:
    /// the client reads the number out of the engine and renders it through the app's own money formatter, which
    /// is also what makes these answers obey privacy mode for free.</summary>
    public static readonly IReadOnlyList<AssistantOption> Topics =
    [
        new("report.spent",        "how much has been spent this period"),
        new("report.budgets",      "how the budgets are doing — what is over, what is close"),
        new("report.topCategory",  "which category has taken the most this period"),
        new("report.saved",        "how much has been set aside this period, and at what rate"),
        new("report.safeToSpend",  "how much is safe to spend right now"),
    ];

    public static bool IsTarget(string? key) => key is not null && Targets.Any(t => t.Key == key);
    public static bool IsExplainer(string? key) => key is not null && Explainers.Any(t => t.Key == key);
    public static bool IsTopic(string? key) => key is not null && Topics.Any(t => t.Key == key);

    /// <summary>The slot kind a target requires, or null when it needs none.</summary>
    public static string? SlotKindFor(string key) => Targets.FirstOrDefault(t => t.Key == key)?.NeedsSlot;
}
