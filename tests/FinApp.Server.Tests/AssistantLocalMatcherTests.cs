using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// The local matcher answers what it plainly can and declines the rest. Two properties matter more than any
/// individual rule, and both are pinned here: <b>it never invents a key the catalogue does not carry</b>, and
/// <b>declining is always available</b> — a question it cannot place costs a model call, not a wrong answer.
/// </summary>
public class AssistantLocalMatcherTests
{
    private static AssistantReplyDto? Match(string q, params string[] slots) =>
        AssistantLocalMatcher.Match(q, slots);

    [Theory]
    [InlineData("how much have i spent this period", "report.spent")]
    [InlineData("what's my safe to spend", "report.safeToSpend")]
    [InlineData("how much can i spend", "report.safeToSpend")]
    [InlineData("which budgets are over", "report.budgets")]
    [InlineData("how much have i set aside", "report.saved")]
    [InlineData("what did i spend the most on", "report.topCategory")]
    public void Questions_about_my_own_figures_are_reports(string question, string topic)
    {
        var reply = Match(question);

        Assert.Equal(AssistantIntents.Report, reply!.Intent);
        Assert.Equal(topic, reply.Target);
    }

    [Theory]
    [InlineData("how does the runway work", "explain.runway")]
    [InlineData("what is safe to spend", "explain.safeToSpend")]
    [InlineData("what does this assistant send", "explain.privacy")]
    [InlineData("how do budgets work", "explain.budgets")]
    [InlineData("why is my debt free date different", "explain.debtFree")]
    [InlineData("how does sharing work", "explain.sharing")]
    public void Questions_about_how_things_work_are_explainers(string question, string key)
    {
        var reply = Match(question);

        Assert.Equal(AssistantIntents.Explain, reply!.Intent);
        Assert.Equal(key, reply.Target);
    }

    [Theory]
    [InlineData("take me to my goals", "tab.goals")]
    [InlineData("show me the week recap", "open.weekRecap")]
    [InlineData("open my achievements", "open.achievements")]
    [InlineData("where are my tags", "open.tags")]
    [InlineData("show trends", "open.trends")]
    [InlineData("goals", "tab.goals")]
    public void Requests_to_go_somewhere_navigate(string question, string target)
    {
        var reply = Match(question);

        Assert.Equal(AssistantIntents.Navigate, reply!.Intent);
        Assert.Equal(target, reply.Target);
    }

    // ── Comparison: the failure that motivated the topic ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("why did my grocery bill jump")]
    [InlineData("am I spending more than last month")]
    [InlineData("how does this compare to last period")]
    [InlineData("has my spending gone up")]
    [InlineData("did it increase")]
    public void Questions_about_change_are_comparisons(string question)
    {
        // ⭐ Every one of these used to answer "I didn't follow that one" — "why" sent them to the explainers,
        // which have nothing to say about change.
        var reply = Match(question);

        Assert.Equal(AssistantIntents.Report, reply!.Intent);
        Assert.Equal("report.compare", reply.Target);
    }

    [Fact]
    public void A_comparison_about_one_category_carries_it()
    {
        // "why did my {1} jump" is an explain-class question by its words and an entity question by its
        // placeholder, and is neither. Comparison is tested before both.
        var reply = Match("why did my {1} bill jump", AssistantSlotKinds.Category);

        Assert.Equal("report.compare", reply!.Target);
        Assert.Equal(1, reply.Slot);
    }

    [Fact]
    public void A_comparison_naming_something_that_is_not_a_category_still_answers()
    {
        // The slot is optional here: a wallet cannot narrow a spending comparison, so it is dropped and the
        // account-wide answer stands rather than the whole question failing.
        var reply = Match("did my {1} go up", AssistantSlotKinds.Wallet);

        Assert.Equal("report.compare", reply!.Target);
        Assert.Null(reply.Slot);
    }

    [Fact]
    public void Ordinary_questions_are_not_dragged_into_comparisons()
    {
        // ⚠️ The guard on a keyword list this eager. "How much have I spent" is a question about now.
        Assert.Equal("report.spent", Match("how much have i spent")!.Target);
        Assert.Equal("explain.periods", Match("how do periods work")!.Target);
        Assert.Equal(AssistantIntents.Navigate, Match("take me to my goals")!.Intent);
    }

    [Theory]
    [InlineData("how long will my money last", "report.runway")]
    [InlineData("when will I run out", "report.runway")]
    [InlineData("when am I debt free", "report.debtFree")]
    [InlineData("when will my loans be paid off", "report.debtFree")]
    [InlineData("what bills are still due", "report.bills")]
    [InlineData("what's coming out this month", "report.bills")]
    public void The_headline_figures_are_reported_not_navigated_to(string question, string topic)
    {
        // ⭐ Each of these used to open a SCREEN. Being shown the runway page when you asked how long the money
        // lasts is an answer to a different question.
        var reply = Match(question);

        Assert.Equal(AssistantIntents.Report, reply!.Intent);
        Assert.Equal(topic, reply.Target);
    }

    [Fact]
    public void Debt_free_is_not_swallowed_by_the_savings_rule()
    {
        // ⚠️ The ordering this protects: "when am I debt free" contains "debt", which the savings rule matches.
        // Answered there it would report a set-aside total against a question about a date.
        Assert.Equal("report.debtFree", Match("when am i debt free")!.Target);
        Assert.Equal("report.saved", Match("how much have i set aside")!.Target);
    }

    [Fact]
    public void One_word_apart_is_a_different_question()
    {
        // ★ The ordering rule this file exists to protect: report is classified before explain, so "my" is what
        // separates a question about your money from a question about the feature.
        Assert.Equal(AssistantIntents.Report, Match("what's my safe to spend")!.Intent);
        Assert.Equal(AssistantIntents.Explain, Match("what is safe to spend")!.Intent);
    }

    [Theory]
    [InlineData(AssistantSlotKinds.Goal, "open.goal")]
    [InlineData(AssistantSlotKinds.Category, "open.category")]
    [InlineData(AssistantSlotKinds.Wallet, "open.wallet")]
    [InlineData(AssistantSlotKinds.Trip, "open.trip")]
    public void A_named_thing_in_the_question_opens_that_thing(string kind, string target)
    {
        var reply = Match("how is my {1} doing", kind);

        Assert.Equal(AssistantIntents.Navigate, reply!.Intent);
        Assert.Equal(target, reply.Target);
        Assert.Equal(1, reply.Slot);
    }

    [Fact]
    public void An_entity_beats_the_generic_answer_for_the_same_words()
    {
        // "how much have I spent" is report.spent; the same question ABOUT something is about that thing, and
        // answering it with the period's whole total would be answering a question nobody asked.
        Assert.Equal("report.spent", Match("how much have i spent")!.Target);
        Assert.Equal("open.category", Match("how much have i spent on {1}", AssistantSlotKinds.Category)!.Target);
    }

    [Fact]
    public void An_explainer_is_not_hijacked_by_a_thing_that_happens_to_be_named()
    {
        // A goal called "budgets" must not turn "how do budgets work" into a row-opening navigation. ⚠️ Note what
        // the right answer actually is: masking has already removed the only word that said which explainer this
        // is, so declining and paying for the model is correct here. What must never happen is opening the row.
        var reply = Match("how do {1} work", AssistantSlotKinds.Goal);

        Assert.True(reply is null || reply.Intent != AssistantIntents.Navigate);
    }

    [Theory]
    [InlineData("what is in my {1}", AssistantSlotKinds.Wallet, "open.wallet")]
    [InlineData("what is left in my {1}", AssistantSlotKinds.Goal, "open.goal")]
    [InlineData("why is my {1} behind", AssistantSlotKinds.Goal, "open.goal")]
    [InlineData("what does my {1} cost", AssistantSlotKinds.Goal, "open.goal")]
    [InlineData("why is {1} so high", AssistantSlotKinds.Category, "open.category")]
    public void An_explain_word_does_not_swallow_the_thing_the_question_named(
        string question, string kind, string expected)
    {
        // ⚠️ These all reached the MODEL before the late entity fallback existed. The class list decides which
        // rule table runs, and an explain-ish opener ("what is", "why") sent them to the explainers — which have
        // nothing to say about a named row — after the entity had already been discarded up front.
        // ⚠️ "what's in my {1}" worked the whole time, purely by accident: the contraction is missing from the
        // Explain list, so it fell to Navigate where the entity rule still ran. Adding the contraction without
        // this fallback broke it, which is how the defect was found.
        var reply = Match(question, kind);

        Assert.NotNull(reply);
        Assert.Equal(expected, reply!.Target);
        Assert.Equal(1, reply.Slot);
    }

    [Theory]
    [InlineData("how do {1} work")]
    [InlineData("how does my {1} work")]
    [InlineData("explain how my {1} works")]
    [InlineData("what does {1} mean")]
    public void A_documentation_verb_still_declines_rather_than_opening_the_row(string question)
    {
        // The other half of the fallback, and the reason it is not simply "entity wins". Masking has removed the
        // word that said WHICH explainer was wanted — a goal named "budgets" makes "how do budgets work" into
        // "how do {1} work" — so declining and paying for the model is correct. Opening the row would answer a
        // question about a feature by showing a savings drawer.
        var reply = Match(question, AssistantSlotKinds.Goal);

        Assert.True(reply is null || reply.Intent != AssistantIntents.Navigate);
    }

    [Fact]
    public void A_real_explainer_still_beats_the_entity_when_its_word_survived_masking()
    {
        // "how do {1} goals work" — the user named a goal AND said "goals", so the explainer table matches before
        // the fallback is ever reached. This is what keeps the fallback from being a behaviour change.
        var reply = Match("how do {1} goals work", AssistantSlotKinds.Goal);

        Assert.Equal(AssistantIntents.Explain, reply?.Intent);
        Assert.Equal("explain.savings", reply?.Target);
    }

    [Fact]
    public void A_slot_index_with_no_kind_behind_it_is_ignored_rather_than_trusted()
    {
        // The masker cannot produce this; a hand-built request can. It must not index past the end.
        var reply = Match("how is my {3} doing", AssistantSlotKinds.Goal);

        Assert.True(reply is null || reply.Target != "open.goal");
    }

    [Theory]
    [InlineData("колко похарчих")]
    [InlineData("отвори бюджет")]
    public void Bulgarian_is_matched_where_the_apps_own_words_are_known(string question)
    {
        Assert.NotNull(Match(question));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("did the parcel arrive")]
    [InlineData("book me a flight to lisbon")]
    [InlineData("asdfgh")]
    public void What_it_cannot_place_it_declines(string question)
    {
        // ★ Declining is the feature. A model call is the cost of a miss; a confident wrong answer is not on offer.
        Assert.Null(Match(question));
    }

    [Fact]
    public void Every_key_it_can_produce_exists_in_the_catalogue()
    {
        // The guard that makes a typo in a rule table impossible to ship. Exercised over a broad sweep of
        // phrasings rather than asserted about the tables, so it covers whatever they become.
        string[] questions =
        [
            "how much have i spent", "what's my safe to spend", "which budgets are over", "how much did i save",
            "what did i spend most on", "how does the runway work", "what is safe to spend", "how do budgets work",
            "what is the health score", "how do periods work", "how do trips work", "what about privacy",
            "how does the bank work", "why is my debt free date wrong", "how does sharing work",
            "take me to my goals", "show spending", "open wallets", "dashboard", "show the week recap",
            "achievements", "health score", "runway", "breakdown", "trends", "bills", "import a statement",
            "review", "archived", "categories", "tags", "invite", "next period", "budgets",
        ];

        foreach (var q in questions)
        {
            if (Match(q) is not { } reply) continue;
            var known = reply.Intent switch
            {
                AssistantIntents.Navigate => AssistantCatalogue.IsTarget(reply.Target),
                AssistantIntents.Explain => AssistantCatalogue.IsExplainer(reply.Target),
                AssistantIntents.Report => AssistantCatalogue.IsTopic(reply.Target),
                _ => false,
            };
            Assert.True(known, $"'{q}' produced {reply.Intent}/{reply.Target}, which is not in the catalogue.");
        }
    }
}
