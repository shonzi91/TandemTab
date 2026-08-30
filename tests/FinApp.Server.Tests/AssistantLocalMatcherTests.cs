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
