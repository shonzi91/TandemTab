using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the Path-B thin-Wallets read model (<see cref="WalletsViewDto"/>) from the domain aggregate. Fund balances
/// are computed here — server-side, from the one domain — so the thin client never runs the money model.
/// </summary>
public static class WalletsMap
{
    private static FundRowDto Row(Account account, Period period, Fund f) => new(
        f.Id,
        f.Name,
        f.Icon,
        f.Note,
        period.FundBalance(f.Id).Amount,
        period.InitialBalances.FirstOrDefault(b => b.FundId == f.Id)?.Amount.Amount ?? 0m,
        f.IsSynced,
        f.IsArchived);

    public static WalletsViewDto View(Account account, long version, decimal? bankBalance = null, string? bankCurrency = null, Period? viewPeriod = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return WalletsViewDto.Empty with { Version = version, Currency = account.Currency };

        var funds = account.RootFunds.Where(f => !f.IsArchived).Select(f => Row(account, period, f)).ToList();
        var archived = account.Funds.Where(f => f.IsRoot && f.IsArchived).Select(f => Row(account, period, f)).ToList();
        var transfers = period.FundTransfers
            .OrderByDescending(t => t.Date)
            .Select(t => new FundTransferRowDto(
                t.Id, t.FromFundId, account.FundName(t.FromFundId), t.ToFundId, account.FundName(t.ToFundId),
                t.Amount.Amount, t.Date, t.Note))
            .ToList();

        return new WalletsViewDto(version, account.Currency, SpendingMap.Overview(account, period, bankBalance, bankCurrency), funds, archived, transfers);
    }
}
