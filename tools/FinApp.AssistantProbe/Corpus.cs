namespace FinApp.AssistantProbe;

/// <summary>
/// Questions written the way a person asks them, deliberately <b>not</b> lifted from the matcher's own rule
/// tables — a corpus copied out of the rules would score 100% and tell you nothing.
/// <para>⚠️ This is a stand-in for the fifteen real unknowns, which are unrecoverable: nothing logs a question.
/// Replace it with the owner's actual phrasings the moment they exist (pass a file), and treat the built-in list
/// as a smoke test of coverage rather than as evidence about real users.</para>
/// </summary>
public static class Corpus
{
    public static readonly List<string> Default =
    [
        // ── The six chips. These must never fall through: they carry their own answers precisely so they cost
        // nothing, and a chip reaching the model is paying to rediscover something written two lines above it.
        "How much have I spent this period?",
        "What's safe to spend?",
        "Which budgets are over?",
        "How does the runway work?",
        "What does this assistant send?",
        "Take me to my goals",

        // ── Navigation, phrased as people phrase it
        "show me my wallets",
        "where do I see my budgets",
        "open the breakdown",
        "i want to see the trends chart",
        "how do I start the next period",
        "where are my archived things",
        "take me to the bank transactions I need to review",
        "I want to invite my partner",
        "where do I manage tags",
        "how do I import a statement",
        "show me last week's recap",
        "what have I achieved",

        // ── Entities. The masker should catch every one of these names.
        "how is my Car fund doing",
        "open Groceries",
        "how much is left in my Emergency fund",
        "show me the Greece trip",
        "what's in my Main account",
        "how much have I spent on Eating out",
        "is my Mortgage on track",

        // ── The known-hard ones, each documented in a commit as having failed once
        "why did my grocery bill jump",
        "am I doing better than last month",
        "when will my loans be paid off",
        "is it worth overpaying the loan",
        "how long will my money last",
        "what bills are still coming out this month",

        // ── Reports, natural phrasing
        "what did I spend the most on",
        "how much have I put aside",
        "am I over budget anywhere",
        "did I spend more on Transport than last month",
        "what's my biggest expense",
        "how much money do I have left",

        // ── Explainers
        "what does safe to spend actually mean",
        "how is the health score calculated",
        "do budgets set money aside",
        "what happens when I start a new period",
        "how does sharing work",
        "when does it check my bank",
        "what is a journey",

        // ── Bulgarian, which the code says is partial on purpose
        "колко похарчих този период",
        "какво е безопасно да похарча",
        "покажи ми целите",
        "защо се увеличи сметката за Храна",
        "колко съм заделил",
        "кога ще изплатя заемите",
        "как работи бюджетът",

        // ── Cross-language: an English word against a Bulgarian category, which is what 5f0b337 built
        "why did my grocery spending go up",
        "how much did I spend on groceries",

        // ── Out of scope, and correctly so. These SHOULD come back unknown; they are the control group, and
        // without them a low fall-through rate cannot be told from a matcher that says yes to everything.
        "did the parcel arrive yet",
        "what's the weather like",
        "add 40 euros for petrol",
        "book me a table for two",
    ];
}
