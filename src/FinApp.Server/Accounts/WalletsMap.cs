using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the Path-B thin-Wallets read model (<see cref="WalletsViewDto"/>) from the domain aggregate. Fund balances
/// are computed here — server-side, from the one domain — so the thin client never runs the money model.
/// </summary>
public static class WalletsMap
{
    private static FundRowDto Row(Account account, Period period, Fund f, Money priorSaved) => new(
        f.Id,
        f.Name,
        f.Icon,
        f.Note,
        period.FundBalance(f.Id).Amount,
        period.InitialBalances.FirstOrDefault(b => b.FundId == f.Id)?.Amount.Amount ?? 0m,
        f.IsSynced,
        f.IsArchived,
        period.AvailableToTransferOutFromFundAfter(f.Id, priorSaved).Amount);

    /// <param name="accountNames">Names of the accounts the caller belongs to, for labelling a transfer's
    /// destination. The aggregate knows only the id it sent money to, so the name has to come from outside it.</param>
    public static WalletsViewDto View(Account account, long version, decimal? bankBalance = null, string? bankCurrency = null,
        Period? viewPeriod = null, IReadOnlyDictionary<Guid, string>? accountNames = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return WalletsViewDto.Empty with { Version = version, Currency = account.Currency };

        // The savings reserved before this period — subtracted from every transfer-out cap so already-saved money
        // isn't offered up to send away (mirrors BudgetingState.PriorSaved).
        var report = new SavingsReportService();
        var priorSaved = report.AccumulatedTotal(account) - period.SavingsNetTotal;

        var funds = account.RootFunds.Where(f => !f.IsArchived).Select(f => Row(account, period, f, priorSaved)).ToList();
        var archived = account.Funds.Where(f => f.IsRoot && f.IsArchived).Select(f => Row(account, period, f, priorSaved)).ToList();
        var transfers = period.FundTransfers
            .OrderByDescending(t => t.Date)
            .Select(t => new FundTransferRowDto(
                t.Id, t.FromFundId, account.FundName(t.FromFundId), t.ToFundId, account.FundName(t.ToFundId),
                t.Amount.Amount, t.Date, t.Note))
            .ToList();

        // Money sent to ANOTHER account. Only the ones that name a destination: an external transfer with no
        // ToAccountId is a disbursement or a plain money-out, which belongs to savings or the ledger, not here.
        var accountTransfers = period.ExternalTransfers
            .Where(t => t.ToAccountId is not null)
            .OrderByDescending(t => t.Date)
            .Select(t => new AccountTransferRowDto(
                t.Id,
                t.AccountTransferId,
                t.FundId,
                account.FundName(t.FundId),
                t.ToAccountId,
                t.ToAccountId is { } to && accountNames is not null && accountNames.TryGetValue(to, out var name) ? name : null,
                t.Amount.Amount,
                t.Date,
                t.Note,
                // Editing rewrites BOTH halves, which needs the pair id both rows carry. A transfer recorded before
                // that link existed has no findable counterpart, so it can only be deleted one-sidedly — the server
                // says which kind this is rather than leaving each client to infer it from a null.
                Editable: t.AccountTransferId is not null))
            .ToList();

        return new WalletsViewDto(version, account.Currency, SpendingMap.Overview(account, period, bankBalance, bankCurrency),
            funds, archived, transfers, accountTransfers);
    }
}
