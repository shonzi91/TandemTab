using FinApp.Domain.Budgeting;
using Xunit;

namespace FinApp.Domain.Tests;

public class BankDuplicateMatcherTests
{
    private static BankDuplicateMatcher.Pending Debit(string id, decimal amount, int day, string? desc = null) =>
        new(id, amount, new DateOnly(2026, 1, day), desc);
    private static BankDuplicateMatcher.Entry Entry(Guid id, decimal amount, int day, string? text = null) =>
        new(id, amount, new DateOnly(2026, 1, day), text);

    [Fact]
    public void Pairs_same_amount_within_the_window_when_the_merchant_matches()
    {
        var exp = Guid.NewGuid();
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -45m, 5, "TESCO STORES 4471 LONDON") },
            new[] { Entry(exp, 45m, 3, "Tesco") });   // 2 days earlier — within ±4, and it names the same shop

        Assert.Single(s);
        Assert.Equal("t1", s[0].ExternalId);
        Assert.Equal(exp, s[0].ExpenseId);
        Assert.Equal(2, s[0].DayGap);
    }

    [Fact]
    public void Ignores_amount_mismatches_and_out_of_window_dates()
    {
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -45m, 5, "TESCO") },
            new[] { Entry(Guid.NewGuid(), 40m, 5, "Tesco"), Entry(Guid.NewGuid(), 45m, 20, "Tesco") });

        Assert.Empty(s);
    }

    [Fact]
    public void Matches_per_occurrence_not_by_existence()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // Two identical bank debits, but only one matching entry → only one pairs.
        var one = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -10m, 4), Debit("t2", -10m, 4) },
            new[] { Entry(a, 10m, 4) });
        Assert.Single(one);

        // Two entries → both pair, each entry claimed once.
        var two = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -10m, 4), Debit("t2", -10m, 4) },
            new[] { Entry(a, 10m, 4), Entry(b, 10m, 5) });
        Assert.Equal(2, two.Count);
        Assert.Equal(2, two.Select(x => x.ExpenseId).Distinct().Count());
    }

    [Fact]
    public void Nearest_date_wins_when_several_entries_share_the_amount()
    {
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -20m, 10, "SHELL") },
            new[] { Entry(far, 20m, 7, "Shell"), Entry(near, 20m, 9, "Shell") });

        Assert.Single(s);
        Assert.Equal(near, s[0].ExpenseId);   // 1 day gap beats 3
    }

    // --- The text rule (the owner's over-eager-duplicates report) -----------------------------

    [Fact]
    public void Two_round_spends_days_apart_with_nothing_in_common_are_not_the_same_spend()
    {
        // The reported case: €10 on the 4th, €10 on the 6th, different shops. Same amount, gap 2 — enough on its
        // own under the old rule, and it told the user they had already logged this.
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -10m, 6, "COSTA COFFEE") },
            new[] { Entry(Guid.NewGuid(), 10m, 4, "Pharmacy") });

        Assert.Empty(s);
    }

    [Fact]
    public void A_silent_entry_still_pairs_on_the_same_day()
    {
        // The manual-log-while-sync-was-down case at its most common: logged the day it was paid, noted nothing.
        // The text says nothing either way, so the tight window carries it.
        var exp = Guid.NewGuid();
        var sameDay = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -32.40m, 6, "SUMUP *TRATTORIA") },
            new[] { Entry(exp, 32.40m, 6, "Dinner") });
        Assert.Single(sameDay);
        Assert.Equal(exp, sameDay[0].ExpenseId);

        // Either side of midnight is still the tight window — a late-evening charge books the next day.
        var nextDay = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -32.40m, 7, "SUMUP *TRATTORIA") },
            new[] { Entry(exp, 32.40m, 6, "Dinner") });
        Assert.Single(nextDay);

        // Three days later, with nothing to vouch for it, it is a separate spend.
        var later = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -32.40m, 9, "SUMUP *TRATTORIA") },
            new[] { Entry(exp, 32.40m, 6, "Dinner") });
        Assert.Empty(later);
    }

    [Fact]
    public void The_same_row_arriving_from_two_sources_still_pairs_across_the_full_window()
    {
        // Statement import then live sync: the second copy carries the bank's own wording, so the merchant vouches
        // for the pair and the full window applies. This is the case the feature was built for.
        var exp = Guid.NewGuid();
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -18.99m, 12, "AMZN Mktp DE*2R41K") },
            new[] { Entry(exp, 18.99m, 9, "AMZN Mktp DE*7T02M") });

        Assert.Single(s);
        Assert.Equal(3, s[0].DayGap);
    }

    [Fact]
    public void Payment_noise_alone_is_not_a_merchant_match()
    {
        // Every card charge says "CARD PAYMENT". If that counted as agreement, the wide window would be back for
        // every pair of rows in the list.
        var s = BankDuplicateMatcher.Suggest(
            new[] { Debit("t1", -25m, 8, "CARD PAYMENT TO SHELL") },
            new[] { Entry(Guid.NewGuid(), 25m, 5, "Card payment") });

        Assert.Empty(s);
    }

    [Fact]
    public void CouldBeSame_is_the_rule_the_auto_file_guard_reads()
    {
        var d6 = new DateOnly(2026, 1, 6);
        var d8 = new DateOnly(2026, 1, 8);

        // Two real charges at one shop, two days apart: not the same transaction on the tight window, so the
        // merchant rule files both instead of dropping the second into manual review.
        Assert.False(BankDuplicateMatcher.CouldBeSame(-10m, d8, "COSTA COFFEE", 10m, d6, "COSTA COFFEE",
            BankDuplicateMatcher.AutoFileWindowDays));
        // The same pair is still offered as a review *suggestion*, which the user can dismiss.
        Assert.True(BankDuplicateMatcher.CouldBeSame(-10m, d8, "COSTA COFFEE", 10m, d6, "COSTA COFFEE"));

        // Same day, same amount — a recurring item already posted it. Held back, as it should be.
        Assert.True(BankDuplicateMatcher.CouldBeSame(-10m, d6, "COSTA COFFEE", 10m, d6, null,
            BankDuplicateMatcher.AutoFileWindowDays));
        // A different amount is never the same transaction, whatever the words say.
        Assert.False(BankDuplicateMatcher.CouldBeSame(-10m, d6, "COSTA COFFEE", 11m, d6, "COSTA COFFEE"));
    }
}
