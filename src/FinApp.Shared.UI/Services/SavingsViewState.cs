using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A <b>thin</b>, domain-free view-model for the Goals/Savings surface (Path-B — docs/MOBILE.md). It holds a cache of
/// the read-model DTOs — every bucket with its server-computed figures (goal progress, debt payoff, investment
/// projection, sinking set-aside) — and mutates through the delta-returning savings-deposit endpoint. No
/// <c>FinApp.Domain</c>, no snapshot, and none of the forecasting math runs on the client.
/// <para>
/// "Add to savings" feels instant via <b>echo-optimism</b>: the bucket's saved figure and the header "saved" rise
/// (and "free" falls) immediately by plain arithmetic, then the server delta reconciles every figure — the progress
/// rings, the projections, the deposit's real id. A failed write reloads.
/// </para>
/// </summary>
public sealed class SavingsViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public AccountOverviewDto Overview { get; private set; } = AccountOverviewDto.Empty;
    public decimal AvailableToSave { get; private set; }
    public List<SavingBucketDto> Buckets { get; private set; } = [];
    public List<SavingDepositRowDto> Deposits { get; private set; } = [];

    /// <summary>Live (non-archived) buckets — the set the picker offers and the tab lists.</summary>
    public IReadOnlyList<SavingBucketDto> ActiveBuckets => Buckets.Where(b => !b.Archived).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId)
    {
        _accountId = accountId;
        Apply(await api.GetSavingsAsync(accountId));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetSavingsAsync(_accountId));

    private void Apply(SavingsViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        Overview = v.Overview;
        AvailableToSave = v.AvailableToSave;
        Buckets = v.Buckets.ToList();
        Deposits = v.Deposits.ToList();
    }

    /// <summary>Set money aside into a bucket: echo the saved figures instantly, then reconcile from the delta.</summary>
    public async Task AllocateAsync(Guid bucketId, decimal amount, string? note)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        BumpSaved(bucketId, +amount);
        // Saved rises and Free falls by the amount (both plain arithmetic). AvailableToSave is deliberately NOT
        // echoed: it's this period's closing balance minus *prior* periods' savings, so a same-period allocation
        // leaves it unchanged — echoing a decrement would flicker before the delta snapped it back.
        Overview = Overview with { Saved = Overview.Saved + amount, Free = Overview.Free - amount };
        Deposits.Insert(0, new SavingDepositRowDto(Guid.NewGuid(), bucketId, BucketName(bucketId), amount, date,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        Raise();
        try { Apply((await api.AddSavingDepositDeltaAsync(_accountId, new AddSavingDepositRequest(bucketId, amount, date, note))).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    private string BucketName(Guid id) => Buckets.FirstOrDefault(b => b.Id == id)?.Name ?? "…";

    private void BumpSaved(Guid bucketId, decimal delta)
    {
        var i = Buckets.FindIndex(b => b.Id == bucketId);
        if (i >= 0) Buckets[i] = Buckets[i] with { Saved = Buckets[i].Saved + delta };
    }
}
