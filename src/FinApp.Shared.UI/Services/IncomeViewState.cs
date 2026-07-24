using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A <b>thin</b>, domain-free view-model for the Income surface (Path B — docs/MOBILE.md). Lists this period's
/// deposits and records new income through the delta-returning <c>POST /deposits</c> endpoint. No
/// <c>FinApp.Domain</c>, no snapshot.
/// <para>
/// Deposits <b>merge server-side</b> by (member, category, fund), so the client can't derive the new merged row
/// locally. We still echo an instant row + nudge <c>Contributed</c> for responsiveness, then reconcile the
/// authoritative overview + version from the delta and re-pull the canonical (merged) list — a small bounded
/// <c>GET /income</c>, never the snapshot. Remove uses the plain deposit endpoint then reconciles the same way.
/// </para>
/// </summary>
public sealed class IncomeViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private int? _periodIndex;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public AccountOverviewDto Overview { get; private set; } = AccountOverviewDto.Empty;
    public List<DepositRowDto> Deposits { get; private set; } = [];
    public IReadOnlyList<CategoryOptionDto> Categories { get; private set; } = [];
    public IReadOnlyList<FundOptionDto> Funds { get; private set; } = [];

    /// <summary>Funds a manual deposit may target — synced (bank-driven) funds are excluded, matching the thick UI.</summary>
    public IReadOnlyList<FundOptionDto> SelectableFunds => Funds.Where(f => !f.Synced).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId, int? periodIndex = null)
    {
        _accountId = accountId;
        _periodIndex = periodIndex;
        Apply(await api.GetIncomeAsync(accountId, periodIndex));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetIncomeAsync(_accountId, _periodIndex));

    private void Apply(IncomeViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        Overview = v.Overview;
        Deposits = v.Deposits.ToList();
        Categories = v.Categories;
        Funds = v.Funds;
    }

    /// <summary>Record income: echo a row + nudge Contributed instantly, then reconcile from the delta + a
    /// canonical re-pull (deposits merge server-side, so the merged row can't be built locally).</summary>
    public async Task AddAsync(Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        Deposits.Insert(0, Echo(categoryId, fundId, amount, date));
        Overview = Overview with { Contributed = Overview.Contributed + amount };
        Raise();
        try
        {
            var delta = await api.AddDepositDeltaAsync(_accountId, new AddDepositRequest(categoryId, fundId, amount, date));
            _version = delta.Version;
            Overview = delta.Overview;   // authoritative totals
            await ReloadAsync();          // canonical merged rows
            Raise();
        }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    /// <summary>Remove a deposit row: drop it instantly, then reconcile the list + overview from the server.</summary>
    public async Task RemoveAsync(Guid depositId)
    {
        var i = Deposits.FindIndex(d => d.Id == depositId);
        if (i >= 0) Deposits.RemoveAt(i);
        Raise();
        try
        {
            await api.RemoveDepositAsync(_accountId, depositId);
            await ReloadAsync();
            Raise();
        }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    // A display-only echo of the row just entered. Labels are a dictionary lookup on the cached picker options —
    // no business logic. MemberName is unknown client-side until the reload lands, so show a placeholder.
    private DepositRowDto Echo(Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        var cat = Categories.FirstOrDefault(c => c.Id == categoryId);
        var fund = Funds.FirstOrDefault(f => f.Id == fundId);
        return new DepositRowDto(Guid.NewGuid(), "…", categoryId,
            categoryId == Guid.Empty ? "General income" : cat?.Name ?? "…", cat?.Icon,
            fundId, fund?.Name ?? "…", amount, date);
    }
}
