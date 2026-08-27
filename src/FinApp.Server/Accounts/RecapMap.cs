using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the thin week-recap read (<see cref="WeeklyRecapViewDto"/>).
///
/// <para>★ <b>This map deliberately owns no rules.</b> Every judgement in the recap — which week is covered, that
/// a disbursement is not negative saving, that carryover is not income, that salary is smoothed into a typical
/// week — lives in <see cref="WeeklyRecapService"/>, which the web already calls directly against the account it
/// holds. All this does is resolve ids to names and icons and flatten <c>Money</c> to decimals, so the two
/// clients are reading the <i>same</i> computation rather than two implementations of the same description.</para>
///
/// <para>That is the difference between this and a port. A ported recap would have been a second set of these
/// rules in Kotlin, drifting the first time either side was edited — and drifting silently, because both would
/// still produce a plausible-looking week.</para>
/// </summary>
public static class RecapMap
{
    /// <param name="account">The account to read.</param>
    /// <param name="today">The <b>caller's</b> local date — it decides which week counts as last completed, and a
    /// server in UTC flips that a day early or late for half the world.</param>
    public static WeeklyRecapViewDto View(Account account, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(account);

        var recap = new WeeklyRecapService().Build(account, today);
        if (recap is null) return WeeklyRecapViewDto.Empty with { Currency = account.Currency };

        string CategoryName(Guid id) => account.FindCategory(id)?.Name ?? "—";
        string? CategoryIcon(Guid id) => account.FindCategory(id)?.Icon;

        var categories = recap.CategoryBreakdown
            .Select(s => new RecapSliceDto(s.Id, CategoryName(s.Id), CategoryIcon(s.Id), s.Total.Amount, s.Count))
            .ToList();

        var tags = recap.TagBreakdown
            .Select(s => new RecapSliceDto(s.Id, account.FindTag(s.Id)?.Name ?? "—", account.FindTag(s.Id)?.Icon,
                s.Total.Amount, s.Count))
            .ToList();

        var biggest = recap.Biggest is { } b
            ? new RecapBiggestDto(b.Amount.Amount, b.CategoryId, CategoryName(b.CategoryId), CategoryIcon(b.CategoryId),
                b.Date, b.Note)
            : null;

        return new WeeklyRecapViewDto(
            account.Currency,
            recap.From,
            recap.To,
            recap.Spent.Amount,
            recap.PreviousSpent.Amount,
            recap.Change.Amount,
            recap.HasComparison,
            recap.TopCategoryId,
            recap.TopCategoryId is { } top ? CategoryName(top) : null,
            recap.TopCategoryId is { } topIcon ? CategoryIcon(topIcon) : null,
            recap.TopCategorySpent.Amount,
            recap.Saved.Amount,
            recap.RoundUpsSaved.Amount,
            recap.ExpenseCount,
            recap.Income.Amount,
            recap.EffectiveIncome.Amount,
            recap.IncomeIsTypical,
            recap.Net.Amount,
            biggest,
            categories,
            tags,
            recap.IsEmpty);
    }
}
