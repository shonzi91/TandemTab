using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A thin, domain-free view-model for the Recurring surface (Path-B — docs/MOBILE.md). Holds the read-model DTOs
/// (bills/income with their due state) and confirms/skips due items through the delta-returning endpoints. The bell
/// actions are occasional, so there's no echo — the write reconciles from the delta's refreshed view (the due state
/// re-derives server-side). A failed write reloads.
/// </summary>
public sealed class RecurringViewState(FinAppApiClient api)
{
    private Guid _accountId;
    private int? _periodIndex;
    private long _version;

    public bool IsReady { get; private set; }
    public string Currency { get; private set; } = "";
    public decimal BillsDue { get; private set; }
    public List<RecurringRowDto> Items { get; private set; } = [];

    public IReadOnlyList<RecurringRowDto> DueItems => Items.Where(i => i.Due).ToList();

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId, int? periodIndex = null)
    {
        _accountId = accountId;
        _periodIndex = periodIndex;
        Apply(await api.GetRecurringAsync(accountId, periodIndex));
        IsReady = true;
        Raise();
    }

    private async Task ReloadAsync() => Apply(await api.GetRecurringAsync(_accountId, _periodIndex));

    private void Apply(RecurringViewDto v)
    {
        _version = v.Version;
        Currency = v.Currency;
        BillsDue = v.BillsDue;
        Items = v.Items.ToList();
    }

    /// <summary>Confirm a due item at its actual amount (posts the real expense/income and marks it handled).</summary>
    public async Task ConfirmAsync(Guid recurringId, decimal actualAmount)
    {
        try { Apply((await api.ConfirmRecurringDeltaAsync(_accountId, recurringId, actualAmount)).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }

    public async Task SkipAsync(Guid recurringId)
    {
        try { Apply((await api.SkipRecurringDeltaAsync(_accountId, recurringId)).View); Raise(); }
        catch { await ReloadAsync(); Raise(); throw; }
    }
}
