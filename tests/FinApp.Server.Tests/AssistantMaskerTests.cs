using FinApp.Contracts;

namespace FinApp.Server.Tests;

/// <summary>
/// The masker is the whole privacy claim of R3, so these tests are written as the claim rather than as a tour of
/// the method: <b>nothing the user named, and no digit, survives into the string that gets sent.</b>
/// </summary>
public class AssistantMaskerTests
{
    private static readonly Guid CarFund = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Joint = Guid.NewGuid();
    private static readonly Guid SkiWeek = Guid.NewGuid();

    private static List<AssistantSlot> Vocabulary() =>
    [
        new(AssistantSlotKinds.Goal, CarFund, "Car fund"),
        new(AssistantSlotKinds.Category, Groceries, "Groceries"),
        new(AssistantSlotKinds.Wallet, Joint, "Joint account"),
        new(AssistantSlotKinds.Trip, SkiWeek, "Ski week"),
        new(AssistantSlotKinds.Trip, SkiWeek, "Chamonix"),   // the same journey, by its destination
    ];

    [Fact]
    public void A_named_goal_never_appears_in_the_text_that_is_sent()
    {
        var masked = AssistantMasker.Mask("how is my Car fund doing", Vocabulary());

        Assert.Equal("how is my {1} doing", masked.Text);
        Assert.DoesNotContain("Car", masked.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AssistantSlotKinds.Goal, Assert.Single(masked.Slots).Kind);
        Assert.Equal(CarFund, masked.Slots[0].Id);
    }

    [Fact]
    public void The_request_carries_slot_kinds_and_never_slot_names()
    {
        var request = AssistantMasker.Mask("open Groceries and my Car fund", Vocabulary()).ToRequest();

        // The one assertion that matters on the wire shape: kinds go, names stay behind.
        Assert.Equal([AssistantSlotKinds.Category, AssistantSlotKinds.Goal], request.Slots);
        Assert.DoesNotContain("Groceries", request.Question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Car fund", request.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_kind_of_named_thing_is_masked_including_a_trips_destination()
    {
        foreach (var (question, kind) in new[]
                 {
                     ("what about Groceries", AssistantSlotKinds.Category),
                     ("what about Car fund", AssistantSlotKinds.Goal),
                     ("what about Joint account", AssistantSlotKinds.Wallet),
                     ("what about Ski week", AssistantSlotKinds.Trip),
                     ("what about Chamonix", AssistantSlotKinds.Trip),
                 })
        {
            var masked = AssistantMasker.Mask(question, Vocabulary());
            Assert.Equal("what about {1}", masked.Text);
            Assert.Equal(kind, masked.Slots[0].Kind);
        }
    }

    [Fact]
    public void Digits_never_travel()
    {
        // The model has no use for a figure, so a number is stripped rather than trusted not to matter.
        var masked = AssistantMasker.Mask("did I spend 4750.20 on Groceries in 2026", Vocabulary());

        Assert.Equal("did I spend ####.## on {1} in ####", masked.Text);
        Assert.DoesNotContain("4750", masked.Text);
        Assert.DoesNotContain("2026", masked.Text);
    }

    [Fact]
    public void The_same_entity_twice_is_one_slot()
    {
        var masked = AssistantMasker.Mask("move Groceries into Groceries", Vocabulary());

        Assert.Equal("move {1} into {1}", masked.Text);
        Assert.Single(masked.Slots);
    }

    [Fact]
    public void The_longest_name_wins_so_half_a_name_is_never_left_behind()
    {
        var vocab = new List<AssistantSlot>
        {
            new(AssistantSlotKinds.Wallet, Guid.NewGuid(), "Car"),
            new(AssistantSlotKinds.Goal, CarFund, "Car fund"),
        };

        var masked = AssistantMasker.Mask("how is my Car fund", vocab);

        Assert.Equal("how is my {1}", masked.Text);
        Assert.Equal(CarFund, masked.Slots[0].Id);
    }

    [Fact]
    public void A_name_inside_a_longer_word_is_not_a_match()
    {
        var masked = AssistantMasker.Mask("is Groceriesland open", Vocabulary());

        Assert.Empty(masked.Slots);
        Assert.Contains("Groceriesland", masked.Text);
    }

    [Fact]
    public void A_name_too_short_to_match_safely_is_left_alone()
    {
        // A wallet called "AB" would otherwise mask the middle of every word containing it.
        var vocab = new List<AssistantSlot> { new(AssistantSlotKinds.Wallet, Guid.NewGuid(), "AB") };

        Assert.Empty(AssistantMasker.Mask("what about ABBA", vocab).Slots);
    }

    [Fact]
    public void An_unrecognised_capitalised_word_is_flagged_rather_than_silently_sent()
    {
        var masked = AssistantMasker.Mask("did I pay Maria back", Vocabulary());

        Assert.False(masked.IsClean);
        Assert.Contains("Maria", masked.Suspect);
    }

    [Fact]
    public void The_first_word_of_a_question_is_not_treated_as_a_name()
    {
        // Otherwise every properly-capitalised sentence would be refused by strict mode, and strict mode that
        // refuses everything is strict mode nobody leaves on.
        Assert.True(AssistantMasker.Mask("What is safe to spend", Vocabulary()).IsClean);
    }

    [Fact]
    public void A_quoted_token_counts_as_suspect_even_in_lower_case()
    {
        var masked = AssistantMasker.Mask("what did I spend at \"corner shop\"", Vocabulary());

        Assert.False(masked.IsClean);
        Assert.Contains("corner", masked.Suspect);
    }

    [Fact]
    public void A_question_with_nothing_to_mask_comes_through_unchanged()
    {
        var masked = AssistantMasker.Mask("what is safe to spend", Vocabulary());

        Assert.Equal("what is safe to spend", masked.Text);
        Assert.Empty(masked.Slots);
        Assert.True(masked.IsClean);
    }
}
