using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the Path-B thin-Spending read-model DTOs from the domain aggregate (see <see cref="SpendingViewDto"/>).
/// Resolving the display fields (category name/icon, fund name) and the overview here — server-side, from the one
/// domain — is the whole point of the slice: the client renders spending without carrying the money model.
/// </summary>
public static class SpendingMap
{
    /// <summary>
    /// The balance-header figures for a period, as the wire DTO. When the account has a bank-synced fund and a
    /// <paramref name="bankBalance"/> is supplied (same currency), <see cref="AccountOverviewDto.Current"/> and
    /// <see cref="AccountOverviewDto.Free"/> are adjusted to reflect the <b>live</b> bank balance — the synced fund's
    /// ledger position is swapped for its real external balance. This mirrors the web client's
    /// <c>BudgetingState.BankAdjust</c> exactly, so a thin client's header matches the thick app's (the money model
    /// itself still uses the conservative ledger figures — this is a display overlay of live external data only).
    /// </summary>
    public static AccountOverviewDto Overview(Account account, Period period, decimal? bankBalance = null, string? bankCurrency = null)
    {
        var ov = AccountOverview.For(account, period);
        var dto = new AccountOverviewDto(
            account.Currency, ov.Current.Amount, ov.Free.Amount, ov.Saved.Amount,
            ov.Spent.Amount, ov.Contributed.Amount, ov.BillsDue.Amount, ov.SafeAfterBills.Amount,
            ov.MoneyIn.Amount, ov.TransfersOut.Amount, ov.SavedThisPeriod.Amount, ov.SavedRate);
        return AdjustForBank(dto, account, period, bankBalance, bankCurrency);
    }

    // Swap the synced fund's ledger balance for its live bank balance in Current/Free (display only). No-op without a
    // synced fund / live balance, and skipped across currencies — identical to BudgetingState.BankAdjust.
    private static AccountOverviewDto AdjustForBank(AccountOverviewDto ov, Account account, Period period, decimal? bankBalance, string? bankCurrency)
    {
        var synced = account.Funds.FirstOrDefault(f => f.IsSynced);
        if (synced is null || bankBalance is not { } live) return ov;
        if (!string.IsNullOrEmpty(bankCurrency) && !string.Equals(bankCurrency, account.Currency, StringComparison.OrdinalIgnoreCase))
            return ov;
        var delta = live - period.LedgerFundBalance(synced.Id).Amount;
        return ov with { Current = ov.Current + delta, Free = ov.Free + delta };
    }

    /// <summary>One expense row with its display fields resolved.</summary>
    public static ExpenseDto ToDto(Account account, Expense e) => new(
        e.Id,
        e.CategoryId,
        account.FindCategory(e.CategoryId)?.Name ?? "—",
        account.FindCategory(e.CategoryId)?.Icon,
        e.FundId,
        account.FundName(e.FundId),
        e.Amount.Amount,
        e.Date,
        e.Note,
        e.AutoFiled,
        e.IsFromSavings,
        e.OnBehalfOfOtherAccount,
        e.IsSettlementSource,
        e.IsSettlementDestination,
        e.InstallmentGroupId,
        e.Part switch
        {
            InstallmentPart.Principal => "principal",
            InstallmentPart.Interest => "interest",
            InstallmentPart.Additional => "additional",
            _ => null,
        },
        e.DebtBucketId,
        e.TripId,
        // Only tags that still exist: a deleted tag leaves its id behind on old rows (see Account.RemoveTag), and a
        // client that rendered those would show a label with no name.
        e.TagIds.Where(t => account.FindTag(t) is not null).ToList(),
        e.Time,
        // The other half of the settlement flags above: who it is with, and how much of this expense has moved.
        // Sent as ids — both accounts are ones the caller is a member of, so a client can name them from the
        // account list it already holds, and the server stays out of the business of labelling other accounts.
        e.SettledToAccountId,
        e.SettledFromAccountId,
        e.SettledAmount,
        e.RefundedAmount);

    /// <summary>The full Spending surface for <paramref name="viewPeriod"/> (the current period when null; empty
    /// currency-only view when the account has no period).</summary>
    public static SpendingViewDto View(Account account, long version, decimal? bankBalance = null, string? bankCurrency = null, Period? viewPeriod = null)
    {
        if ((viewPeriod ?? account.CurrentPeriod) is not { } period)
            return SpendingViewDto.Empty with { Version = version, Currency = account.Currency };

        // Newest first, and within a day newest-on-the-clock first. Untimed rows report TimeOnly.MinValue, so they
        // fall to the bottom of their own day rather than jumping to the top of it (see Expense.SortTime).
        var expenses = period.Expenses
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.SortTime)
            .Select(e => ToDto(account, e))
            .ToList();

        // Pickers show active categories/funds; a manual expense can't target a synced (bank) fund, but we send the
        // flag rather than filtering so the client can label/disable it — the same choice the current UI makes.
        var categories = account.Categories
            .Where(c => !c.IsArchived)
            .Select(c => new CategoryOptionDto(c.Id, c.Name, c.Icon, c.ParentId))
            .ToList();
        var funds = account.RootFunds
            .Where(f => !f.IsArchived)
            .Select(f => new FundOptionDto(f.Id, f.Name, f.IsSynced))
            .ToList();
        var tags = account.ActiveTags
            .Select(t => new TagOptionDto(t.Id, t.Name, t.Icon, t.CategoryId, t.IsTripTag))
            .ToList();

        return new SpendingViewDto(version, account.Currency, Overview(account, period, bankBalance, bankCurrency), expenses, categories, funds, tags);
    }
}
