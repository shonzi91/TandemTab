using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A <b>thin</b>, domain-free view-model for the Wallets/Funds surface (Path-B — docs/MOBILE.md). It holds a cache of
/// the read-model DTOs (funds + balances + this period's transfers) and mutates through the delta-returning fund
/// endpoints. No <c>FinApp.Domain</c>, no snapshot — balances are computed server-side.
/// <para>
/// Writes feel instant via <b>echo-optimism</b>: a transfer moves the amount between the two funds' cached balances
/// immediately and prepends the transfer row (plain arithmetic on non-synced funds — the only ones the picker offers —
/// not the money model), then the server delta reconciles the whole view (authoritative balances, the row's real id).
/// A failed write reloads.
/// </para>
/// </summary>
public sealed class WalletsViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public AccountOverviewDto Overview { get; private set; } = AccountOverviewDto.Empty;
    public List<FundRowDto> Funds { get; private set; } = [];
    public IReadOnlyList<FundRowDto> ArchivedFunds { get; private set; } = [];
    public List<FundTransferRowDto> Transfers { get; private set; } = [];

    /// <summary>Funds a manual transfer may target — synced (bank-driven) funds are excluded, matching the thick UI.</summary>
    public IReadOnlyList<FundRowDto> SelectableFunds => Funds.Where(f => !f.Synced).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId)
    {
        _accountId = accountId;
        Apply(await api.GetWalletsAsync(accountId));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetWalletsAsync(_accountId));

    private void Apply(WalletsViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        Overview = v.Overview;
        Funds = v.Funds.ToList();
        ArchivedFunds = v.ArchivedFunds;
        Transfers = v.Transfers.ToList();
    }

    /// <summary>Move money between two funds: echo the balances + the row instantly, then reconcile from the delta.</summary>
    public async Task TransferAsync(Guid fromFundId, Guid toFundId, decimal amount, string? note)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        BumpBalance(fromFundId, -amount);
        BumpBalance(toFundId, +amount);
        Transfers.Insert(0, new FundTransferRowDto(Guid.NewGuid(), fromFundId, FundName(fromFundId),
            toFundId, FundName(toFundId), amount, date, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        Raise();
        try { Apply((await api.TransferFundsDeltaAsync(_accountId, new TransferFundsRequest(fromFundId, toFundId, amount, date, note))).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    /// <summary>Add a fund: echo an empty row instantly, then reconcile (adopting the server's id).</summary>
    public async Task AddFundAsync(string name, string? note, string? icon)
    {
        Funds.Add(new FundRowDto(Guid.NewGuid(), name, icon, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), 0m, 0m, false, false));
        Raise();
        try { Apply((await api.AddFundDeltaAsync(_accountId, new CreateFundRequest(name, null, note, icon))).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    private string FundName(Guid id) => Funds.FirstOrDefault(f => f.Id == id)?.Name ?? "…";

    private void BumpBalance(Guid fundId, decimal delta)
    {
        var i = Funds.FindIndex(f => f.Id == fundId);
        if (i >= 0) Funds[i] = Funds[i] with { Balance = Funds[i].Balance + delta };
    }
}
