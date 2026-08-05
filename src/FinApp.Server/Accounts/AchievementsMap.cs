using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>Builds the Path-B thin-Achievements read model (<see cref="AchievementsViewDto"/>) from the domain's
/// <see cref="AchievementsService"/> — the same catalogue that drives the Home milestones counts, so the panel and
/// the count share one source. English copy (identity translate); earned dates from the account's achievement log
/// (best-effort — null when the thick Dashboard hasn't stamped one). See AchievementsView.cs for the i18n note.</summary>
public static class AchievementsMap
{
    public static AchievementsViewDto View(Account account)
    {
        static string Fmt(Money m) => MoneyText.Format(m.Amount, m.Currency);
        var all = new AchievementsService().Build(account, Fmt);   // identity translate → English
        var log = account.AchievementLog;

        var items = all
            .Select(a => new AchievementDto(
                a.Key, a.Icon, a.Title, a.Desc, a.Earned, a.Percent, a.Tier.ToString(),
                log.TryGetValue(a.Key, out var d) ? d : null))
            .ToList();

        return new AchievementsViewDto(
            items.Count(i => i.Earned),
            items.Count,
            items.Count(i => !i.Earned && i.Percent is > 0),
            items);
    }
}
