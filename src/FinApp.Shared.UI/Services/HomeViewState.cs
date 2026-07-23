using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// A <b>thin</b>, domain-free view-model for the Home surface (Path-B — docs/MOBILE.md). It loads the computed Home
/// figures from their read endpoints — <c>overview</c>, <c>runway</c>, <c>targets</c>, <c>milestones</c>,
/// <c>insights</c> — and exposes them for direct binding. No <c>FinApp.Domain</c>, no snapshot: the server owns every
/// figure, so nothing here can drift from the money model.
/// <para>
/// Home is read-only (its action cards delegate to the Spending/income writes), so there's no optimism to do — a
/// mutation elsewhere calls <see cref="RefreshAsync"/> to re-pull the affected figures. The five reads fire in
/// parallel; a runway of <c>null</c> is the real "no basis to project" state, distinct from zeroed figures.
/// </para>
/// </summary>
public sealed class HomeViewState(FinAppApiClient api)
{
    private Guid _accountId;

    public bool IsReady { get; private set; }
    public AccountOverviewDto Overview { get; private set; } = AccountOverviewDto.Empty;
    public RunwayDto? Runway { get; private set; }
    public TargetsDto Targets { get; private set; } = TargetsDto.Empty;
    public MilestonesDto Milestones { get; private set; } = MilestonesDto.Empty;
    public InsightsDto Insights { get; private set; } = InsightsDto.Empty;

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public async Task LoadAsync(Guid accountId)
    {
        _accountId = accountId;
        await RefreshAsync();
        IsReady = true;
        Raise();
    }

    /// <summary>Re-pull every Home figure (call after a write on another surface). The reads are independent, so they
    /// go in parallel — one round-trip's worth of latency, not five.</summary>
    public async Task RefreshAsync()
    {
        var overview = api.GetOverviewAsync(_accountId);
        var runway = api.GetRunwayAsync(_accountId);
        var targets = api.GetTargetsAsync(_accountId);
        var milestones = api.GetMilestonesAsync(_accountId);
        var insights = api.GetInsightsAsync(_accountId);
        await Task.WhenAll(overview, runway, targets, milestones, insights);
        Overview = overview.Result;
        Runway = runway.Result;
        Targets = targets.Result;
        Milestones = milestones.Result;
        Insights = insights.Result;
        Raise();
    }
}
