using System.Net;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Forecasting;
using FinApp.Forecasting;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Recurring;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;
using Microsoft.JSInterop;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Application state the Blazor UI binds to, backed by the sync server. Holds the signed-in user's
/// account summaries, the loaded full aggregate for the selected account, and the period being viewed.
/// Writes go through the server's command endpoints (the Option-A cutover): each mutation POSTs a command,
/// then re-fetches the snapshot so the local aggregate reflects the server's authoritative result. The
/// domain still renders the reads locally; it just no longer applies the writes. A few flows without
/// endpoints yet (bank confirms, achievements stamping, account settings) still mutate locally and push
/// the whole snapshot — each is marked with a TODO(cutover).
/// </summary>
public sealed class BudgetingState(FinAppApiClient api, AuthState auth, SyncClient sync, IJSRuntime js)
{
    private readonly BudgetCoverageService _coverage = new();
    private readonly SavingsReportService _savings = new();
    private readonly RoundUpService _roundUps = new();
    private readonly WeeklyRecapService _recap = new();

    // Remembers the last account the user was on, so a reload lands back where they left off (not always the first).
    private const string LastAccountKey = "finapp-last-account";

    private List<AccountSummaryDto> _summaries = [];
    private Account? _account;
    private long _version;
    private int _accountIndex;
    private int _selectedIndex;
    private bool _syncStarted;
    private List<InvitationDto> _pendingInvitations = [];

    // Per-account aggregate cache so switching back to an already-loaded account is instant (no re-fetch).
    // It's only trusted while live sync is connected: AccountChanged drops a changed account's entry, and a
    // reconnect clears everything (events during the outage are missed). Falls back to always-fetch when offline.
    private sealed class CachedAccount(Account account, long version)
    {
        public Account Account { get; } = account;
        public long Version { get; set; } = version;
    }
    private readonly Dictionary<Guid, CachedAccount> _cache = [];

    public bool IsReady { get; private set; }
    public event Action? Changed;

    // Monotonic counter bumped on every state change (every Changed). Lets consumers cheaply memoize expensive
    // derived views (e.g. the Insights report) and recompute only when the underlying data actually changed —
    // not on every render caused by a UI-only toggle (opening a modal, the bell, switching tabs).
    private long _revision;
    public long Revision => _revision;

    /// <summary>Bump the change counter and raise <see cref="Changed"/>. All internal mutations go through this.</summary>
    private void RaiseChanged() { _revision++; Changed?.Invoke(); }

    public async Task InitializeAsync()
    {
        if (IsReady || !auth.IsAuthenticated) return;

        if (!_syncStarted)
        {
            sync.AccountChanged += OnAccountChanged;
            sync.InvitationReceived += OnInvitationReceived;
            sync.Reconnected += OnReconnected;
            try { await sync.StartAsync(); } catch { /* live sync is best-effort; REST still works */ }
            _syncStarted = true;
        }

        _summaries = await api.GetAccountsAsync();
        // Land on the account the user last had open (persisted in the browser), falling back to the first.
        var lastId = await ReadLastAccountAsync();
        var restored = lastId is { } lid ? _summaries.FindIndex(a => a.Id == lid) : -1;
        _accountIndex = restored >= 0 ? restored : 0;
        await SubscribeAllAsync();   // so AccountChanged invalidates cached background accounts too
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();

        IsReady = true;
        RaiseChanged();
    }

    /// <summary>Clear all session state on sign-out.</summary>
    public async Task ResetAsync()
    {
        IsReady = false;
        _summaries = [];
        _account = null;
        _version = 0;
        _accountIndex = 0;
        _selectedIndex = 0;
        _pendingInvitations = [];
        _syncStarted = false;
        _cache.Clear();
        sync.AccountChanged -= OnAccountChanged;
        sync.InvitationReceived -= OnInvitationReceived;
        sync.Reconnected -= OnReconnected;
        await sync.StopAsync();
        RaiseChanged();
    }

    // --- Accounts ---------------------------------------------------------

    public bool HasAccounts => _summaries.Count > 0;
    public Account Account => _account!;
    public IReadOnlyList<AccountSummaryDto> Accounts => _summaries;
    public Guid CurrentAccountId => _account?.Id ?? Guid.Empty;

    /// <summary>True when the signed-in user owns the current account (gates rename/delete/member removal). Trusts the
    /// server-authoritative summary owner (<see cref="CurrentOwnerId"/>) rather than the opaque snapshot's OwnerUserId:
    /// an ownership transfer updates the relational header but not the client-owned snapshot blob, so a new owner would
    /// otherwise stay locked out of owner actions. Falls back to the snapshot only when no summary is loaded yet.</summary>
    public bool IsOwnerOfCurrent => auth.UserId != Guid.Empty &&
        (CurrentOwnerId != Guid.Empty ? CurrentOwnerId == auth.UserId : _account?.IsOwner(auth.UserId) == true);

    public async Task SwitchAccount(Guid accountId)
    {
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index < 0 || index == _accountIndex) return;
        _accountIndex = index;
        await LoadSelectedAccountAsync();
        await RememberSelectedAccountAsync();
        RaiseChanged();
    }

    // Persist / restore the last-open account id so a reload returns to it. Best-effort — storage may be unavailable.
    private async Task RememberSelectedAccountAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", LastAccountKey, CurrentAccountId.ToString()); }
        catch { /* storage unavailable — the first account is a fine fallback */ }
    }

    private async Task<Guid?> ReadLastAccountAsync()
    {
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", LastAccountKey);
            return Guid.TryParse(saved, out var id) ? id : null;
        }
        catch { return null; }
    }

    public async Task AddAccount(string name, string currency, decimal savingsRateTarget = 0.20m)
    {
        if (_summaries.Any(a => NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        var summary = await api.CreateAccountAsync(new CreateAccountRequest(name, currency));
        _summaries.Add(summary);
        _accountIndex = _summaries.Count - 1;
        await LoadSelectedAccountAsync(); // empty snapshot -> the server bootstraps the starter body
        if (savingsRateTarget != _account!.SavingsRateTarget)
        {
            _account.SetSavingsRateTarget(savingsRateTarget);
            await PushSnapshotAsync();
        }
        await RememberSelectedAccountAsync();
        RaiseChanged();
    }

    /// <summary>The account's target savings rate (fraction 0..1) — drives the Insights gauge/score.</summary>
    public decimal SavingsRateTarget => Account.SavingsRateTarget;

    /// <summary>Set the account's target savings rate (fraction 0..1) and push the snapshot.</summary>
    // TODO(cutover): needs a command endpoint (account settings) — still local-mutate + whole-snapshot push.
    public Task SetSavingsRateTarget(decimal target)
    {
        Account.SetSavingsRateTarget(target);
        return SaveAsync();
    }

    // --- Time cost: reading an amount as the hours behind it ----------------------------------------------------

    /// <summary>A rate typed by hand, or null when the user hasn't set one (they may still be deriving it).</summary>
    public decimal? HourlyRate => Account.HourlyRate;

    public int? WorkingDaysPerMonth => Account.WorkingDaysPerMonth;
    public decimal? WorkingHoursPerDay => Account.WorkingHoursPerDay;

    /// <summary>The rate actually in use: typed if there is one, else derived from this period's income.</summary>
    public decimal? EffectiveHourlyRate => Account.EffectiveHourlyRate;

    /// <summary>New deposits this period — the numerator when the rate is derived from income.</summary>
    public decimal IncomeThisPeriod => Period.ContributionsPaidTotal.Amount;

    /// <summary>True when the rate in use is being computed from income rather than typed.</summary>
    public bool HourlyRateIsDerived => Account.HourlyRate is null && Account.EffectiveHourlyRate is not null;

    /// <summary>Set (or clear with null/0) the hourly rate and push the snapshot.</summary>
    // TODO(cutover): needs a command endpoint (account settings) — still local-mutate + whole-snapshot push.
    public Task SetHourlyRate(decimal? rate)
    {
        Account.SetHourlyRate(rate);
        return SaveAsync();
    }

    /// <summary>Set (or clear) the working pattern the rate is derived from, and push the snapshot.</summary>
    public Task SetWorkingPattern(int? daysPerMonth, decimal? hoursPerDay)
    {
        Account.SetWorkingPattern(daysPerMonth, hoursPerDay);
        return SaveAsync();
    }

    /// <summary>
    /// "≈ 2h 15m of work" for an amount, or null when no rate is set (in which case nothing is drawn — a blank is
    /// better than a guess). Hours and minutes only: days would need a working-day length this app has no business
    /// assuming, and an hourly rate is an estimate that shouldn't be dressed up in precision it doesn't have.
    /// </summary>
    public string? TimeCostLabel(decimal amount)
    {
        if (Account.TimeCostOf(amount) is not { } span || span.TotalMinutes < 1) return null;
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        return hours == 0 ? $"{minutes}m" : minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    /// <summary>F7 — "your week in money" for the last completed week, or null when there's nothing to report.</summary>
    public WeeklyRecap? WeeklyRecap() => _recap.Build(Account, Today());

    // --- F4 round-ups -----------------------------------------------------

    /// <summary>The round-up step (0 = off, 1 or 5) and the bucket the change goes into.</summary>
    public decimal RoundUpTo => Account.RoundUpTo;
    public Guid? RoundUpBucketId => Account.RoundUpBucketId;
    public bool RoundUpsOn => Account.RoundUpsOn;

    /// <summary>What an expense of this amount would set aside — for previewing the figure without spending.</summary>
    public decimal RoundUpFor(decimal amount) => Account.RoundUpFor(amount);

    /// <summary>Everything round-ups have set aside across the account's whole history — the line that makes the
    /// feature worth having switched on. Identified by the sweep's note, which nothing else writes.</summary>
    public Money RoundUpsSweptTotal =>
        Money(Account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Where(a => a.Note == RoundUpService.SweepNote)
            .Sum(a => a.Amount.Amount));

    /// <summary>Round-ups actually swept into savings within [from, to] — the factual "you set aside €X painlessly"
    /// figure for the Breakdown window. Free: it's the user's own money.</summary>
    public Money RoundUpsSweptInRange(DateOnly from, DateOnly to) =>
        Money(Account.Periods
            .SelectMany(p => p.SavingAllocations)
            .Where(a => a.Note == RoundUpService.SweepNote && a.Date >= from && a.Date <= to)
            .Sum(a => a.Amount.Amount));

    /// <summary>Turn round-ups on (step 1 or 5 + a destination bucket) or off (step 0), and persist.</summary>
    // TODO(cutover): needs a command endpoint (account settings) — still local-mutate + whole-snapshot push.
    public Task ConfigureRoundUps(decimal roundUpTo, Guid? bucketId)
    {
        Account.ConfigureRoundUps(roundUpTo, bucketId);
        return SaveAsync();
    }

    /// <summary>User closed the Home "Getting started" checklist — persist so it stays gone.</summary>
    // TODO(cutover): needs a command endpoint (account settings) — still local-mutate + whole-snapshot push.
    public Task DismissOnboarding()
    {
        Account.DismissOnboarding();
        return SaveAsync();
    }

    public async Task RenameAccount(string name)
    {
        var id = CurrentAccountId;
        if (_summaries.Any(a => a.Id != id && NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        await api.RenameAccountAsync(id, name);
        _account!.Rename(name);
        _summaries[_accountIndex] = _summaries[_accountIndex] with { Name = name };
        RaiseChanged();
    }

    public async Task RemoveAccount(Guid accountId)
    {
        await api.DeleteAccountAsync(accountId);
        _cache.Remove(accountId);
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index >= 0) _summaries.RemoveAt(index);
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        RaiseChanged();
    }

    // --- Membership / archiving -------------------------------------------

    /// <summary>The current account's other members (everyone but the signed-in user).</summary>
    public IReadOnlyList<MemberDto> OtherMembers => RealUsers.Where(m => m.UserId != auth.UserId).ToList();

    public Guid MyUserId => auth.UserId;

    /// <summary>The current account's owner (from the server-authoritative summary).</summary>
    public Guid CurrentOwnerId => _summaries.ElementAtOrDefault(_accountIndex)?.OwnerUserId ?? Guid.Empty;

    /// <summary>Leave the current account. Returns whether it was archived (you were the last member) or just left.</summary>
    public async Task<LeaveAccountResult> LeaveCurrentAccount(Guid? newOwnerUserId)
    {
        var id = CurrentAccountId;
        var result = await api.LeaveAccountAsync(id, newOwnerUserId);
        _cache.Remove(id);
        var index = _summaries.FindIndex(a => a.Id == id);
        if (index >= 0) _summaries.RemoveAt(index);   // dropped from the active list either way
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        RaiseChanged();
        return result;
    }

    /// <summary>Owner removes another member from the current account.</summary>
    public async Task RemoveMember(Guid memberUserId)
    {
        var id = CurrentAccountId;
        await api.RemoveMemberAsync(id, memberUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    /// <summary>Owner hands ownership of the current account to another member.</summary>
    public async Task TransferOwnership(Guid newOwnerUserId)
    {
        var id = CurrentAccountId;
        await api.TransferOwnershipAsync(id, newOwnerUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    public Task<List<ArchivedAccountDto>> GetArchivedAccounts() => api.GetArchivedAccountsAsync();

    public async Task ReactivateAccount(Guid accountId)
    {
        await api.ReactivateAccountAsync(accountId);
        await ReloadSummariesKeepingAsync(accountId);
        await LoadSelectedAccountAsync(forceRefresh: true);
        RaiseChanged();
    }

    private async Task ReloadSummariesKeepingAsync(Guid accountId)
    {
        _summaries = await api.GetAccountsAsync();
        var idx = _summaries.FindIndex(a => a.Id == accountId);
        _accountIndex = idx >= 0 ? idx : Math.Max(0, Math.Min(_accountIndex, _summaries.Count - 1));
    }

    private async Task LoadSelectedAccountAsync(bool forceRefresh = false)
    {
        if (_summaries.Count == 0) { _account = null; _version = 0; return; }

        var summary = _summaries[_accountIndex];

        // Load member profile pictures for this account (fire-and-forget so account switching stays instant).
        if (_avatarsAccountId != summary.Id)
        {
            _avatarsAccountId = summary.Id;
            _ = RefreshMemberAvatarsAsync(summary.Id);
        }

        // Warm-cache hit: render the already-loaded aggregate instantly, no server round-trip. Only trusted
        // while live sync is connected (otherwise we can't know if a contributor changed it behind our back).
        if (!forceRefresh && sync.IsConnected && _cache.TryGetValue(summary.Id, out var hit))
        {
            _account = hit.Account;
            _version = hit.Version;
            ReconcileHeader(_account, summary);
            _selectedIndex = _account.Periods.Count - 1;
            return;
        }

        var snapshot = await api.GetSnapshotAsync(summary.Id);
        _version = snapshot.Version;

        if (string.IsNullOrEmpty(snapshot.Payload))
        {
            // Brand-new account: the server seeds the starter body (the same Account.SeedStarter the web used to
            // run locally, so accounts start byte-identically). Today = the caller's local date, so the first
            // period lands on the user's month. 409 = another device/tab won the race; just fetch what it made.
            try { await api.BootstrapAccountAsync(summary.Id, Today()); }
            catch (ApiException e) when (e.Status == HttpStatusCode.Conflict) { /* already seeded */ }
            snapshot = await api.GetSnapshotAsync(summary.Id);
            _version = snapshot.Version;
        }
        _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        ReconcileHeader(_account, summary);

        _cache[summary.Id] = new CachedAccount(_account, _version);
        _selectedIndex = _account.Periods.Count - 1;
        // Anchor achievements to "now" the first time this account is ever loaded, so they count from the current
        // period onward (not retroactively) and can't be farmed by back-dating periods. Persisted on the next save.
        if (_account.AchievementsAnchor is null && _account.CurrentPeriod is { } cp)
            _account.SetAchievementsAnchor(cp.From);
        await sync.SubscribeAsync(summary.Id);
    }

    /// <summary>Stamp any newly-earned achievement with today's date and persist — but only when something actually
    /// changed, so it's safe to call after every render (it converges). Returns true if the snapshot was saved.</summary>
    // TODO(cutover): needs a command endpoint (record achievement) — still local-mutate + whole-snapshot push.
    private (Guid acct, long rev)? _stampedAt;

    public async Task<bool> StampAchievementsAsync(AchievementsService svc, Func<Money, string> fmt, Func<string, string> t)
    {
        if (!IsReady || !HasAccounts) return false;
        var acct = Account;
        // Achievements are entirely data-derived, so they can only newly-earn when the aggregate changes. Skip the
        // (periods × achievements) rebuild when nothing has changed since the last stamp — this runs after every render.
        var mark = (acct.Id, _revision);
        if (_stampedAt == mark) return false;
        _stampedAt = mark;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var changed = false;
        foreach (var a in svc.Build(acct, fmt, t))
            if (a.Earned && !acct.AchievementLog.ContainsKey(a.Key)) { acct.RecordAchievement(a.Key, today); changed = true; }
        if (changed) await SaveAsync();
        return changed;
    }

    /// <summary>
    /// Every milestone tied to a specific savings or debt bucket that is currently earned (F6). Deliberately <b>not</b>
    /// "newly stamped": the achievement log lives in the shared account snapshot, so once one member's client records a
    /// milestone the other member pulls it down already-logged and would never see it as new. Whether <i>you</i> have
    /// been shown the moment is a fact about you, not about the account, so the caller tracks that per device.
    /// </summary>
    public static IReadOnlyList<Achievement> BucketMilestones(IReadOnlyList<Achievement> all) =>
        all.Where(a => a.Earned && IsBucketMilestone(a.Key)).ToList();

    /// <summary>Per-bucket milestone keys (<c>goal_{id}</c>, <c>debt_{tier}_{id}</c>) — the ones that belong to one
    /// goal a household is working on together, as opposed to the general catalogue medals.
    /// <para>The trailing id must parse as a Guid rather than the prefix being enough: <c>debt_half_all</c> is a
    /// catalogue medal about <i>all</i> debt and shares the <c>debt_</c> prefix, so a prefix test would celebrate it
    /// as though it belonged to one bucket.</para></summary>
    private static bool IsBucketMilestone(string key) =>
        (key.StartsWith("goal_", StringComparison.Ordinal) || key.StartsWith("debt_", StringComparison.Ordinal))
        && Guid.TryParse(key[(key.LastIndexOf('_') + 1)..], out _);

    /// <summary>Ensure the loaded aggregate reflects server-authoritative header data (name + members).</summary>
    private static void ReconcileHeader(Account account, AccountSummaryDto summary)
    {
        if (account.Name != summary.Name) account.Rename(summary.Name);
        foreach (var m in summary.Members)
            if (!account.IsContributor(m.UserId))
                account.AddMember(m.UserId, m.DisplayName);
    }

    /// <summary>Guards <see cref="PushSnapshotAsync"/> so only one push is in flight at a time. Two overlapping
    /// pushes would both send the <em>same</em> <c>_version</c> (the first hasn't returned to advance it yet), and
    /// the server rejects the loser as a conflict — reported to the user as "someone else updated this account",
    /// which was reachable with a single user in a single tab: SaveAsync raises Changed before awaiting its push, so
    /// the re-render runs mid-flight and the achievement stamp / recurring auto-post in OnAfterRenderAsync can start
    /// a second push off the stale version.</summary>
    private readonly SemaphoreSlim _pushLock = new(1, 1);

    /// <summary>Timing of the last snapshot push. Every mutation rewrites the whole account, so this is what we
    /// need before reshaping storage: it splits the cost into serialize (CPU, scales with history), upload+server
    /// (bytes and round-trip) and reports the payload size driving both. Guessing between those picks the wrong
    /// fix — chunking storage does nothing if the cost is a fixed per-request overhead, and vice versa.</summary>
    public sealed record SaveTiming(int PayloadBytes, double SerializeMs, double UploadMs, long WaitedMs);

    /// <summary>Timing of the most recent push (null until one completes). See <see cref="SaveTiming"/>.</summary>
    public SaveTiming? LastSave { get; private set; }

    /// <summary>Serialize the current aggregate and push it to the server, advancing the version.</summary>
    private async Task PushSnapshotAsync()
    {
        var queued = System.Diagnostics.Stopwatch.StartNew();
        await _pushLock.WaitAsync();
        var waited = queued.ElapsedMilliseconds;   // time spent behind another push — a queue, not the save itself
        try
        {
            // Serialized inside the lock, so a queued push sends the latest aggregate against the version the
            // push ahead of it just established (both callers mutate the same live Account instance).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var payload = AccountSnapshotSerializer.Serialize(_account!);
            var serializeMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var saved = await api.SaveSnapshotAsync(_account!.Id, new SaveAccountRequest(payload, _version));
            var uploadMs = sw.Elapsed.TotalMilliseconds;

            _version = saved.Version;
            // Keep the cache entry's version in step with our own push (the Account is the same live instance).
            if (_cache.TryGetValue(_account.Id, out var c)) c.Version = _version;
            else _cache[_account.Id] = new CachedAccount(_account, _version);

            LastSave = new SaveTiming(payload.Length, serializeMs, uploadMs, waited);
            // Goes to the browser console — readable in devtools on a real account, which is the only place the
            // history is big enough for the answer to mean anything.
            Console.WriteLine($"[save] payload={payload.Length}B serialize={serializeMs:0.#}ms upload={uploadMs:0.#}ms queued={waited}ms v={_version}");
        }
        finally { _pushLock.Release(); }
    }

    /// <summary>
    /// The command-write spine, no optimism: run a mutation endpoint against the current account, then re-fetch the
    /// snapshot so the local aggregate reflects the server's authoritative result. Used by the writes that can't be
    /// applied locally first — the two-account settlement/transfer writes (the other account isn't loaded) and the
    /// batch import. Most writes use <see cref="ExecuteOptimisticAsync"/> instead, which repaints instantly.
    /// </summary>
    private async Task<MutationResultDto> ExecuteAsync(Func<Guid, Task<MutationResultDto>> command)
    {
        var accountId = CurrentAccountId;
        // Share the one push lock with PushSnapshotAsync so a deferred whole-snapshot PUT (achievements stamp,
        // settings) can't run mid-command and collide with the server-side change.
        await _pushLock.WaitAsync();
        try
        {
            var result = await command(accountId);
            await RefreshFromServerAsync(accountId);
            return result;
        }
        finally { _pushLock.Release(); }
    }

    /// <summary>
    /// The optimistic command-write spine. Applies <paramref name="optimistic"/> to the loaded aggregate and repaints
    /// <b>immediately</b> (the same domain the server runs, so the figures match), then sends <paramref name="command"/>
    /// in the background. The domain is still on-device for the reads this slice, so applying it for the write too costs
    /// nothing extra and removes the round-trip latency the user would otherwise feel on every action.
    /// <para>
    /// The flag follows a deliberately conservative rule so no id can silently drift: <b>deletes</b> pass
    /// <paramref name="refetchAfter"/> false — they mint no id and touch only an already-agreed one, so we skip the
    /// re-fetch and just advance the version from the command result (one round-trip). <b>Everything else</b> (creates,
    /// and edits that are append-only so they mint a fresh id) passes true and re-fetches in the background to adopt
    /// the server's canonical ids; the UI has already painted so that round-trip isn't felt, but callers must read the
    /// <b>server</b> id from the returned result, never the optimistic one. <b>On failure</b>: re-fetch to roll the
    /// optimistic change back to server truth, then the exception propagates so the UI shows the error.
    /// </para>
    /// </summary>
    private async Task<MutationResultDto> ExecuteOptimisticAsync(Action optimistic, Func<Guid, Task<MutationResultDto>> command, bool refetchAfter)
    {
        // Hold the push lock for the whole operation, taken BEFORE the optimistic apply. This is the crux of mixing
        // optimistic local mutations with the deferred whole-snapshot PUT: the optimistic change lives on the same
        // _account that PushSnapshotAsync serializes, so a PUT that fired between the local apply and the command's
        // confirmation would persist the not-yet-confirmed mutation AND the command would re-apply it — a duplicate.
        // Serialising through the one lock means no PUT runs until the command has confirmed (refetched, or the
        // delete settled), so the whole-snapshot path only ever sees server-consistent state.
        await _pushLock.WaitAsync();
        try
        {
            var accountId = CurrentAccountId;
            optimistic();
            RaiseChanged();                 // instant repaint from the local apply (the render is queued, not sync)
            MutationResultDto result;
            try
            {
                result = await command(accountId);
            }
            catch
            {
                await RefreshFromServerAsync(accountId);   // undo the optimistic change — server is the truth
                throw;
            }
            if (refetchAfter)
            {
                await RefreshFromServerAsync(accountId);   // adopt the server's entity ids
            }
            else if (accountId == CurrentAccountId && result.Version >= _version)
            {
                _version = result.Version;                 // keep optimistic concurrency in step without a round-trip
                if (_cache.TryGetValue(accountId, out var c)) c.Version = _version;
            }
            return result;
        }
        finally { _pushLock.Release(); }
    }

    /// <summary>Re-fetch and swap in the server's snapshot for <paramref name="accountId"/>. Drops stale results:
    /// a refresh that lost a race to a newer one (higher version already loaded) or to an account switch is ignored.</summary>
    private async Task RefreshFromServerAsync(Guid accountId)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (accountId != CurrentAccountId || string.IsNullOrEmpty(snapshot.Payload)) return;
        if (snapshot.Version < _version) return;
        _version = snapshot.Version;
        _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        ReconcileHeader(_account, _summaries[_accountIndex]);
        _cache[accountId] = new CachedAccount(_account, _version);
        _selectedIndex = Math.Min(_selectedIndex, _account.Periods.Count - 1);
        RaiseChanged();
    }

    // --- Period navigation ------------------------------------------------

    public Period Period => Account.Periods[_selectedIndex];
    public int PeriodNumber => _selectedIndex + 1;
    public int PeriodCount => Account.Periods.Count;
    public bool CanGoPrev => _selectedIndex > 0;
    public bool CanGoNext => _selectedIndex < Account.Periods.Count - 1;
    public bool IsLatestPeriod => _selectedIndex == Account.Periods.Count - 1;

    /// <summary>How many completed periods the runway's demonstrated average is built on — the same filter
    /// <see cref="CashFlowHistory.Demonstrated"/> uses, surfaced so the runway can name its basis ("based on your
    /// last N months") instead of presenting a projection as a certainty.</summary>
    public int CompletedPeriodCount => Account.Periods.Count(p => p.Status == PeriodStatus.Closed);

    /// <summary>You can only roll into the next period once the current one has actually ended — this blocks creating
    /// future periods in advance (which would let milestones/streaks be farmed). Viewing past periods stays allowed.</summary>
    public bool CanStartNextPeriod =>
        Account.CurrentPeriod is { } p && p.To < DateOnly.FromDateTime(DateTime.Today);

    public void GoPrev() { if (CanGoPrev) { _selectedIndex--; RaiseChanged(); } }
    public void GoNext() { if (CanGoNext) { _selectedIndex++; RaiseChanged(); } }

    public string Currency => Account.Currency;
    public Money Money(decimal amount) => new(amount, Currency);

    // --- Funds ------------------------------------------------------------

    public IReadOnlyList<Fund> Funds => Account.Funds;
    /// <summary>Active (non-archived) root funds — the pickers and the "where your money is" list. Kept as
    /// <c>RootFunds</c> for call-site compatibility; archived funds are hidden here but still resolve by name/id.</summary>
    public IReadOnlyList<Fund> RootFunds => Account.RootFunds.Where(f => !f.IsArchived).ToList();

    /// <summary>Archived root funds (hidden from the main list) — for the collapsible "archived" section.</summary>
    public IReadOnlyList<Fund> ArchivedFunds => Account.Funds.Where(f => f.IsRoot && f.IsArchived).ToList();
    public bool FundIsArchived(Guid fundId) => Account.FindFund(fundId)?.IsArchived ?? false;
    public Fund? FindFund(Guid fundId) => Account.FindFund(fundId);
    public string? FundNote(Guid fundId) => Account.FindFund(fundId)?.Note;
    public string FundName(Guid fundId) => Account.FundName(fundId);
    public Money FundBalance(Guid fundId) => Period.FundBalance(fundId);
    public Money FundOpeningBalance(Guid fundId) =>
        Period.InitialBalances.FirstOrDefault(b => b.FundId == fundId)?.Amount ?? Money(0);
    public string? FundRemovalBlocker(Guid fundId) => Account.FundRemovalBlocker(fundId);

    public IReadOnlyList<FundTransfer> FundTransfers =>
        Period.FundTransfers.OrderByDescending(t => t.Date).ToList();

    // The web default fund for a spend/drawdown: first selectable (non-synced, non-archived) root fund, matching
    // what the server derives when the request's FundId is empty — so the optimistic apply picks the same one.
    private Guid DefaultFundId => SelectableFunds.FirstOrDefault()?.Id ?? Account.RootFunds.FirstOrDefault()?.Id ?? Guid.Empty;

    /// <summary>The period's opening balance: the sum of the real (non-informative) initial fund values.
    /// Independent of how the money is later budgeted/saved (unallocations never change it).</summary>
    public Money OpeningBalance => Period.InitialTotal;

    /// <summary>Physical money expected to carry into the next period.</summary>
    public Money ClosingBalance => Period.ExpectedClosingBalance;

    /// <summary>The period's closing total the way the Wallets per-fund rows show it: each synced fund's captured
    /// closing balance (its carried-in opening is stored <i>informative</i>, so the ledger holds it at 0 and it's
    /// absent from <see cref="ClosingBalance"/>) plus every other fund's ledger balance. On a CLOSED period this equals
    /// the sum of the fund rows — so the "Closed with" headline and the Wallets donut agree with them, where the plain
    /// <see cref="ClosingBalance"/> understated by synced funds' carry-in. Open periods use
    /// <see cref="DisplayClosingBalance"/> (live bank-adjust) instead.</summary>
    public Money ClosedFundTotal =>
        Money(RootFunds.Sum(f =>
            (FundIsSynced(f.Id) ? (SyncedFundClosingBalance(f.Id) ?? FundBalance(f.Id)) : FundBalance(f.Id)).Amount));

    /// <summary>The account total (and free-to-allocate) for display, with the synced fund's ledger position swapped
    /// for its <b>live</b> bank balance so the header reflects real external money (incl. transactions not yet
    /// imported). Display-only: allocation caps keep using the conservative ledger figures. No-op without a synced
    /// fund / live balance, and skipped when the bank reports a different currency (we don't add across currencies).</summary>
    public Money DisplayClosingBalance(decimal? liveBankBalance, string? bankCurrency) => BankAdjust(ClosingBalance, liveBankBalance, bankCurrency);
    public Money DisplayFreeToAllocate(decimal? liveBankBalance, string? bankCurrency) => BankAdjust(FreeToAllocate, liveBankBalance, bankCurrency);

    /// <summary>The cash you can still move into savings, shown in the Add-to-savings modal. Same basis as the header
    /// "free" (bank-adjusted) but floored at 0 — you can't set aside a negative amount. That floor is the only reason
    /// it can read lower than "free": when you've already earmarked more than you hold, free goes negative but this
    /// stays 0.</summary>
    public Money AvailableToSaveDisplay(decimal? liveBankBalance, string? bankCurrency)
    {
        var free = DisplayFreeToAllocate(liveBankBalance, bankCurrency);
        return free.IsNegative ? Money(0m) : free;
    }
    private Money BankAdjust(Money baseAmount, decimal? liveBankBalance, string? bankCurrency)
    {
        if (!HasSyncedFund || liveBankBalance is not { } live) return baseAmount;
        if (!string.IsNullOrEmpty(bankCurrency) && !string.Equals(bankCurrency, Account.Currency, StringComparison.OrdinalIgnoreCase))
            return baseAmount;
        return baseAmount + Money(live - Period.LedgerFundBalance(SyncedFundId).Amount);
    }

    /// <summary>This period's transfers sent out to other accounts (newest first).</summary>
    public IReadOnlyList<ExternalTransfer> ExternalTransfers =>
        Period.ExternalTransfers.OrderByDescending(t => t.Date).ToList();

    // --- Category tree & budgets (reads) ----------------------------------

    // Pickers and the budget tree show only active (non-archived) categories; archived ones stay resolvable by
    // name/id (via FindCategory/CategoryName) so historical expenses keep their label.
    public IEnumerable<Category> RootCategories => Account.RootCategories.Where(c => !c.IsArchived);
    public IEnumerable<Category> ChildrenOf(Guid parentId) => Account.ChildrenOfCategory(parentId).Where(c => !c.IsArchived);
    public IReadOnlyList<Category> AllCategories => Account.Categories.Where(c => !c.IsArchived).ToList();

    /// <summary>Root categories that are archived (hidden from the tree) — for a collapsible "archived" section.</summary>
    public IReadOnlyList<Category> ArchivedCategories => Account.Categories.Where(c => c.IsRoot && c.IsArchived).ToList();

    /// <summary>Categories in tree order with their depth, for an indented &lt;select&gt; (parents above their children).</summary>
    public IReadOnlyList<(Category Category, int Depth)> CategoryOptions
    {
        get
        {
            var result = new List<(Category, int)>();
            void Walk(IEnumerable<Category> nodes, int depth)
            {
                foreach (var c in nodes.Where(c => !c.IsArchived))
                {
                    result.Add((c, depth));
                    Walk(Account.ChildrenOfCategory(c.Id), depth + 1);
                }
            }
            Walk(Account.RootCategories, 0);
            return result;
        }
    }
    public Budget? BudgetFor(Guid categoryId) => Period.FindBudget(categoryId);
    public bool HasBudget(Guid categoryId) => Period.FindBudget(categoryId) is not null;
    public BudgetCoverage Coverage(Guid categoryId) => _coverage.ForCategory(Account, Period, categoryId);
    public Money Leftover(Guid categoryId) => Coverage(categoryId).Remaining;
    public string? CategoryRemovalBlocker(Guid categoryId) => Account.CategoryRemovalBlocker(categoryId);
    public string CategoryName(Guid categoryId) => Account.FindCategory(categoryId)?.Name ?? "—";
    public string? ParentName(Guid? parentId) => parentId is { } p ? Account.FindCategory(p)?.Name : null;

    public IEnumerable<Category> BudgetedCategories =>
        Period.Budgets.Select(b => Account.FindCategory(b.CategoryId)!).Where(c => c is not null);

    /// <summary>Total spent in a category and its sub-categories this period (works without a budget).</summary>
    public Money SpentInCategory(Guid categoryId)
    {
        var ids = Account.CategoryWithDescendantIds(categoryId).ToHashSet();
        return Period.Expenses.Where(e => ids.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money(0), (acc, m) => acc + m);
    }

    /// <summary>How many expenses were logged in a category and its sub-categories this period.</summary>
    public int ExpenseCountInCategory(Guid categoryId)
    {
        var ids = Account.CategoryWithDescendantIds(categoryId).ToHashSet();
        return Period.Expenses.Count(e => ids.Contains(e.CategoryId));
    }

    // --- Totals & reports -------------------------------------------------

    public Money TotalBudgeted => Period.BudgetedTotal;
    public Money TotalSpent => Period.ExpensesTotal;

    /// <summary>All money that left the account this period for the Home "Spent" tile: expenses plus plain
    /// account-to-account transfers (excludes savings disbursements). <see cref="TotalSpent"/> stays expenses-only
    /// for budget contexts — a transfer isn't budget spend.</summary>
    public Money TotalMoneyOut => Period.ExpensesTotal + Period.AccountTransfersOutTotal;

    /// <summary>The transfer half of <see cref="TotalMoneyOut"/> on its own, so the hero "Spent" card can break
    /// its own total down ("+X transfers") the way "Money in" breaks out carry-over. Without this the tile silently
    /// mixes two very different kinds of money-out and reads as overspending.</summary>
    public Money TransfersOutThisPeriod => Period.AccountTransfersOutTotal;

    /// <summary>New member deposits this period (the contributed pool).</summary>
    public Money TotalContributed => Period.ContributionsPaidTotal;

    /// <summary>Savings earmarked beyond actual cash left — overspend to reconcile next period.</summary>
    public Money Deficit => Period.Deficit;
    public bool HasDeficit => Period.Deficit.Amount > 0m;

    /// <summary>
    /// Savings accumulated <b>before</b> this period — total saved across the whole account (incl. pre-app initial
    /// balances) minus this period's own net. The opening balances carry that money forward, so the planning caps
    /// must reserve it: otherwise previously-saved money looks freshly available to budget, save or transfer again.
    /// </summary>
    private Money PriorSaved => _savings.AccumulatedTotal(Account) - Period.SavingsNetTotal;

    /// <summary>Most that can be sent to another account without breaking the savings earmark.</summary>
    public Money AvailableToTransferOut => Period.AvailableToTransferOutAfter(PriorSaved);

    /// <summary>Most that can be sent to another account from a specific fund (≤ that fund's balance).</summary>
    public Money AvailableToTransferOutFromFund(Guid fundId) => Period.AvailableToTransferOutFromFundAfter(fundId, PriorSaved);
    // Display figures ("saved this period" / "total saved") exclude disbursements — deploying a save to its goal
    // counts as saved. The money model above (PriorSaved) keeps using the earmark totals, which do drop.
    public Money SavingsThisPeriod => Period.SavingsSetAsideTotal;
    public Money SavingsAccumulated => _savings.LifetimeSaved(Account);
    public Money MaxAdditionalSavings => Period.MaxAdditionalSavingsAfter(PriorSaved);
    public Money AvailableToSave => Period.AvailableToSaveAfter(PriorSaved);

    // "Money in" for the savings rate (domain-owned in SavingsReportService). Fresh income alone is the wrong
    // denominator: setting aside *carried-over* cash, divided by this period's income, over-states the rate (it can
    // even exceed 100%). So the rate is measured against everything you had to work with — fresh income + free carry-in.
    /// <summary>Money that actually arrived this period (member deposits/income), excluding carried-over balance.</summary>
    public Money FreshInThisPeriod => Period.ContributionsPaidTotal;
    /// <summary>Free cash carried in from before (opening balance minus what's already earmarked for savings).</summary>
    public Money CarriedInThisPeriod => MoneyInThisPeriod - FreshInThisPeriod;
    /// <summary>All the money you had to work with this period = fresh income + free carry-in. Denominator for the rate.</summary>
    public Money MoneyInThisPeriod => _savings.MoneyIn(Account, Period);

    /// <summary>The period after the selected one, if any — so a closed period can show what it handed forward.</summary>
    public Period? NextPeriod => _selectedIndex + 1 < Account.Periods.Count ? Account.Periods[_selectedIndex + 1] : null;
    /// <summary>Free cash carried INTO <paramref name="p"/> from the period before it (its money-in minus its own fresh
    /// income) — includes synced-fund carry, so it matches the "+X carried" that period displays. Used to show what a
    /// just-closed period carried to the next.</summary>
    public Money CarriedInto(Period p) => _savings.MoneyIn(Account, p) - p.ContributionsPaidTotal;
    /// <summary>Saved this period as a fraction of money-in (null when nothing came in). Naturally bounded to ~0–100%,
    /// since you can't set aside more than came in — unlike the old "% of income", which carry-over could inflate.</summary>
    public decimal? PeriodMoneyInRate => _savings.PeriodMoneyInRate(Account, Period);

    /// <summary>Unallocated cash this period (closing − all savings). Negative = over-allocated. Advisory only.</summary>
    public Money FreeToAllocate => Period.FreeToAllocateAfter(PriorSaved);
    public bool IsOverAllocated => Period.FreeToAllocateAfter(PriorSaved).IsNegative;

    /// <summary>The most a single category's budget can be set to (Current − savings + spent, minus other budgets). Caps budgeting.</summary>
    public Money MaxBudgetFor(Guid categoryId) => Period.MaxBudgetFor(categoryId, PriorSaved);

    public IReadOnlyList<Expense> AllExpenses =>
        Period.Expenses.OrderByDescending(e => e.Date).ToList();

    public IReadOnlyList<Expense> ExpensesFor(Guid categoryId) =>
        Period.Expenses.Where(e => e.CategoryId == categoryId).OrderByDescending(e => e.Date).ToList();

    /// <summary>Every expense across ALL periods whose date falls in [from, to] — the basis for the Breakdown view's
    /// multi-period windows (3/6/12 months, all-time, custom). Newest first.</summary>
    public IReadOnlyList<Expense> ExpensesInRange(DateOnly from, DateOnly to) =>
        Account.Periods.SelectMany(p => p.Expenses)
            .Where(e => e.Date >= from && e.Date <= to)
            .OrderByDescending(e => e.Date).ToList();

    /// <summary>Every out-transfer to another account across ALL periods in [from, to] — money that left the account,
    /// so the Breakdown view can show it alongside expenses as part of total outflow. Newest first.</summary>
    public IReadOnlyList<ExternalTransfer> ExternalTransfersInRange(DateOnly from, DateOnly to) =>
        Account.Periods.SelectMany(p => p.ExternalTransfers)
            .Where(t => t.Date >= from && t.Date <= to)
            .OrderByDescending(t => t.Date).ToList();

    /// <summary>Account-to-account transfers in [from, to] — like <see cref="ExternalTransfersInRange"/> but WITHOUT
    /// savings disbursements (a bucket payout isn't spending). The Breakdown's "money out" / "Transfers out" slice uses
    /// this so it agrees with the Home "Spent" tile (<see cref="TotalMoneyOut"/>), which also excludes disbursements.</summary>
    public IReadOnlyList<ExternalTransfer> AccountTransfersInRange(DateOnly from, DateOnly to) =>
        Account.Periods.SelectMany(p => p.AccountTransfersOut)
            .Where(t => t.Date >= from && t.Date <= to)
            .OrderByDescending(t => t.Date).ToList();

    /// <summary>Savings disbursements in [from, to] — the money-out leg of a bucket payout (set-aside money deployed
    /// toward a goal/debt). The complement of <see cref="AccountTransfersInRange"/> within all external transfers
    /// (<c>ExternalTransfers</c> minus <c>AccountTransfersOut</c>, same instances → reference set-difference). Deploying
    /// set-aside money is an achievement, not spending, so it's kept out of the "Spent" total and gets its own positive
    /// "Saved toward goals" Breakdown slice.</summary>
    public IReadOnlyList<ExternalTransfer> DisbursementsInRange(DateOnly from, DateOnly to) =>
        Account.Periods.SelectMany(p => p.ExternalTransfers.Except(p.AccountTransfersOut))
            .Where(t => t.Date >= from && t.Date <= to)
            .OrderByDescending(t => t.Date).ToList();

    /// <summary>The top-level category an expense rolls up to (a sub-category's parent, else the category itself).
    /// Categories are capped at one level deep, so parent-or-self is enough.</summary>
    public Guid RootCategoryId(Guid categoryId) => Account.FindCategory(categoryId)?.ParentId ?? categoryId;

    /// <summary>Earliest expense date on record (across all periods) — the "beginning of time" for the all-time window.</summary>
    public DateOnly? EarliestExpenseDate =>
        Account.Periods.SelectMany(p => p.Expenses).Select(e => (DateOnly?)e.Date).Min();

    /// <summary>Earliest of any expense OR member contribution on record — the true start of the "all time" window.
    /// Anchoring only on the first expense undercounted income when money came in before the first spend (so all-time
    /// income didn't match a wider fixed window like 12 months); this captures both sides.</summary>
    public DateOnly? EarliestActivityDate
    {
        get
        {
            var expenses = Account.Periods.SelectMany(p => p.Expenses).Select(e => (DateOnly?)e.Date);
            var income = Account.Periods.SelectMany(p => p.Contributions)
                .Where(c => c.MemberId != FinApp.Domain.Periods.Period.CarryoverSource)
                .Select(c => (DateOnly?)c.Date);
            return expenses.Concat(income).Min();
        }
    }

    /// <summary>Total income (member contributions, excluding carryover) across all periods in [from, to] — pairs with
    /// <see cref="ExpensesInRange"/> so the Breakdown view can show income for the same window.</summary>
    public decimal IncomeInRange(DateOnly from, DateOnly to) =>
        Account.Periods.SelectMany(p => p.Contributions)
            .Where(c => c.MemberId != FinApp.Domain.Periods.Period.CarryoverSource && c.Date >= from && c.Date <= to)
            .Sum(c => c.Paid.Amount);

    public bool IsPeriodOpen => Period.Status == PeriodStatus.Open;

    public Expense? FindExpense(Guid id) => Period.Expenses.FirstOrDefault(e => e.Id == id);

    // --- Faster expense entry (#11) --------------------------------------------------------------
    // Manual entries only (AutoFiled bank rows reflect the bank, not a deliberate choice), newest first:
    // periods newest→oldest, and within a period the last-added expense first (list order ≈ entry order).
    private IEnumerable<Expense> ManualExpensesNewestFirst =>
        Enumerable.Reverse(Account.Periods.ToList())
            .SelectMany(p => Enumerable.Reverse(p.Expenses.ToList()))
            .Where(e => !e.AutoFiled);

    /// <summary>The most recent manual expense, for the "repeat last" quick action. Null when none logged yet.</summary>
    public Expense? LastExpense => ManualExpensesNewestFirst.FirstOrDefault();

    /// <summary>The fund last used for a category (across all periods), to default the fund picker to what the user
    /// actually tends to pay it from. Only returns a fund that's still selectable; null when there's no usable history.</summary>
    public Guid? LastFundForCategory(Guid categoryId)
    {
        var selectable = SelectableFunds.Select(f => f.Id).ToHashSet();
        return ManualExpensesNewestFirst
            .Where(e => e.CategoryId == categoryId && selectable.Contains(e.FundId))
            .Select(e => (Guid?)e.FundId)
            .FirstOrDefault();
    }

    /// <summary>Recent distinct merchants (expense notes), newest first, each carrying the category/fund/amount from
    /// its most recent use — for one-tap "recent merchant" chips in the add-expense modal.</summary>
    public IReadOnlyList<Expense> RecentMerchants(int max = 6) =>
        ManualExpensesNewestFirst
            .Where(e => !string.IsNullOrWhiteSpace(e.Note))
            .GroupBy(e => e.Note!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(max)
            .ToList();

    /// <summary>Recently-used expense categories for one-tap entry — most-used first, recency breaking ties. Only
    /// categories that still exist (not archived/deleted); auto-filed bank rows are excluded (see the stream above).</summary>
    public IReadOnlyList<Guid> RecentCategories(int max = 6)
    {
        var live = AllCategories.Select(c => c.Id).ToHashSet();
        var stats = new Dictionary<Guid, (int Count, int FirstSeen)>();
        var i = 0;
        foreach (var e in ManualExpensesNewestFirst)
        {
            if (!live.Contains(e.CategoryId)) { i++; continue; }
            if (stats.TryGetValue(e.CategoryId, out var v)) stats[e.CategoryId] = (v.Count + 1, v.FirstSeen);
            else stats[e.CategoryId] = (1, i);
            i++;
        }
        return stats
            .OrderByDescending(kv => kv.Value.Count)   // most used first
            .ThenBy(kv => kv.Value.FirstSeen)          // then most recently used (stream is newest-first)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Amounts recently spent in a category — most-used first, recency breaking ties — as one-tap <b>hints</b> for
    /// the Add-expense amount field (F1's booster).
    /// <para>
    /// Deliberately hints and never a pre-fill: an amount is the one field that genuinely changes every time, so a
    /// wrong default here is a wrong <i>ledger entry</i>, not a wrong guess the user notices. Distinct values only,
    /// and a value must have been used at least twice to qualify — a one-off €13.47 is history, not a habit, and
    /// offering it as a shortcut is noise.
    /// </para>
    /// </summary>
    public IReadOnlyList<decimal> RecentAmountsForCategory(Guid categoryId, int max = 3)
    {
        if (categoryId == Guid.Empty) return [];
        var stats = new Dictionary<decimal, (int Count, int FirstSeen)>();
        var i = 0;
        foreach (var e in ManualExpensesNewestFirst)
        {
            if (e.CategoryId != categoryId) continue;
            var amount = e.Amount.Amount;
            if (amount <= 0m) { i++; continue; }
            if (stats.TryGetValue(amount, out var v)) stats[amount] = (v.Count + 1, v.FirstSeen);
            else stats[amount] = (1, i);
            i++;
        }
        return stats
            .Where(kv => kv.Value.Count >= 2)          // twice = a habit; once = history
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.FirstSeen)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>The funds you log expenses against most often, most-used first (ties broken by most-recently-used),
    /// restricted to funds you can still spend from. Powers the Add-expense quick-pick chips.</summary>
    public IReadOnlyList<Guid> RecentFunds(int max = 4)
    {
        var live = ExpensableFunds.Select(f => f.Id).ToHashSet();
        var stats = new Dictionary<Guid, (int Count, int FirstSeen)>();
        var i = 0;
        foreach (var e in ManualExpensesNewestFirst)
        {
            if (!live.Contains(e.FundId)) { i++; continue; }
            if (stats.TryGetValue(e.FundId, out var v)) stats[e.FundId] = (v.Count + 1, v.FirstSeen);
            else stats[e.FundId] = (1, i);
            i++;
        }
        return stats
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.FirstSeen)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>The category to pre-fill for an imported expense with this description: reuse the one from the most
    /// recent past expense whose note matches (same normalization the bank sync uses), so a merchant you've filed
    /// before auto-files again. Null when nothing matches — the review then leaves it on the default.</summary>
    public Guid? SuggestExpenseCategory(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var key = BankMatchKey(description);
        return key.Length == 0 ? null : ManualExpensesNewestFirst
            .Where(e => !string.IsNullOrWhiteSpace(e.Note) && BankMatchKey(e.Note!) == key)
            .Select(e => (Guid?)e.CategoryId)
            .FirstOrDefault();
    }

    public decimal? PeriodSavingsRate => _savings.PeriodSavingsRate(Period);
    public decimal? AccountSavingsRate => _savings.AccountSavingsRate(Account);

    public SavingCategory? FindSavingBucket(Guid id) => Account.FindSavingCategory(id);
    public SavingGoalProgress SavingGoal(Guid bucketId) => _savings.GoalProgress(Account, bucketId);
    public string? SavingBucketRemovalBlocker(Guid id) => Account.SavingCategoryRemovalBlocker(id);

    public string MemberName(Guid memberId) =>
        Account.Members.FirstOrDefault(m => m.UserId == memberId)?.DisplayName ?? "—";

    /// <summary>The real signed-in users on this account (the server-authoritative header members — owner + invited
    /// contributors). Excludes members that only exist inside the imported snapshot (no real user behind them).</summary>
    public IReadOnlyList<MemberDto> RealUsers =>
        _summaries.ElementAtOrDefault(_accountIndex)?.Members ?? [];

    /// <summary>True when this member id belongs to a real signed-in user (vs a snapshot-imported placeholder).</summary>
    public bool IsRealUser(Guid memberId) => RealUsers.Any(m => m.UserId == memberId);

    // Member profile pictures (server-stored), loaded per account.
    private Guid _avatarsAccountId;
    private Dictionary<Guid, string> _memberAvatars = [];

    /// <summary>The member's profile picture (data-URL), or null to fall back to initials.</summary>
    public string? MemberAvatar(Guid memberId) =>
        _memberAvatars.TryGetValue(memberId, out var v) ? v : null;

    private async Task RefreshMemberAvatarsAsync(Guid accountId)
    {
        try
        {
            var avatars = await api.GetAccountAvatarsAsync(accountId);
            if (_avatarsAccountId == accountId) { _memberAvatars = avatars; RaiseChanged(); }
        }
        catch { /* best effort — fall back to initials */ }
    }

    /// <summary>Drop cached member avatars (e.g. after the signed-in user changes their own picture).</summary>
    public void InvalidateMemberAvatars() => _avatarsAccountId = Guid.Empty;

    public IReadOnlyList<(SavingCategory Bucket, Money Total)> SavingBuckets =>
        Account.SavingCategories
            .Select(b => (b, _savings.ForBucket(Account, Period, b.Id).AccumulatedTotal))
            .ToList();

    public string SavingBucketName(Guid id) => FindSavingBucket(id)?.Name ?? "—";

    /// <summary>True when the account holds a live investment bucket with a positive expected return — the gate for
    /// the runway's "Additional income" slider. We only offer the extra-income scenario to someone whose money is
    /// already set up to grow, so it reads as "model your investment income", not "you ought to earn more".</summary>
    public bool HasEarningInvestment =>
        Account.SavingCategories.Any(b => !b.IsArchived && b.IsInvestment && b.InvestmentAnnualRatePercent > 0m);

    /// <summary>Rough money-in per month the account's investments throw off right now: each earning bucket's balance
    /// times its annual rate, spread over twelve months. Seeds the runway's "Additional income" slider so the
    /// investments a member already holds are on it by default (simple interest — the compounding projection lives on
    /// the bucket itself).</summary>
    public decimal InvestmentMonthlyEarnings =>
        decimal.Round(SavingBuckets
            .Where(b => !b.Bucket.IsArchived && b.Bucket.IsInvestment && b.Bucket.InvestmentAnnualRatePercent > 0m)
            .Sum(b => b.Total.Amount * b.Bucket.InvestmentAnnualRatePercent / 100m / 12m), 2);

    /// <summary>This period's manual "Add to savings" deposits, newest first (editable/removable).</summary>
    public IReadOnlyList<SavingAllocation> SavingDepositsThisPeriod =>
        Period.ManualSavingDeposits().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingDeposit(Guid id) =>
        Period.ManualSavingDeposits().FirstOrDefault(a => a.Id == id);

    /// <summary>This period's savings spendings (money matured into a budget, or moved between buckets), newest first.</summary>
    public IReadOnlyList<SavingAllocation> SavingMovementsThisPeriod =>
        Period.SavingMovements().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingMovement(Guid id) =>
        Period.SavingMovements().FirstOrDefault(a => a.Id == id);

    /// <summary>A human-readable destination for a savings movement row (a budget category, or another bucket).</summary>
    public string SavingMovementTarget(SavingAllocation movement)
    {
        if (movement.BudgetCategoryId is { } categoryId)
            return $"{SavingBucketName(movement.SavingCategoryId)} → {CategoryName(categoryId)} (budget)";
        if (movement.TransferPairId is { } pairId)
        {
            var toId = Period.SavingAllocations
                .Where(a => a.TransferPairId == pairId && !a.Amount.IsNegative)
                .Select(a => a.SavingCategoryId)
                .FirstOrDefault();
            return $"{SavingBucketName(movement.SavingCategoryId)} → {SavingBucketName(toId)} (bucket)";
        }
        if (movement.IsDisbursement)
            return $"{SavingBucketName(movement.SavingCategoryId)} → {(string.IsNullOrWhiteSpace(movement.Note) ? "goal" : movement.Note)}";
        return SavingBucketName(movement.SavingCategoryId);
    }

    // TODO(cutover): no PUT /savings/movements endpoint yet — still local-mutate + whole-snapshot push.
    public Task EditSavingMovement(Guid allocationId, decimal amount)
    {
        Period.EditSavingMovement(allocationId, Money(amount));
        return SaveAsync();
    }

    public Task RemoveSavingMovement(Guid allocationId) =>
        ExecuteOptimisticAsync(() => Period.RemoveSavingMovement(allocationId),
            id => api.RemoveSavingMovementAsync(id, allocationId), refetchAfter: false);

    public IReadOnlyList<AccountMember> Members => Account.Members;
    public Contribution? ContributionFor(Guid memberId) =>
        Period.Contributions.FirstOrDefault(c => c.MemberId == memberId);

    /// <summary>Who the current actions are attributed to — the signed-in user (a member of the account).</summary>
    private Guid CurrentMemberId => auth.UserId;

    // --- Contribution categories + itemized deposits ----------------------
    public IReadOnlyList<ContributionCategory> ContributionCategories => Account.ContributionCategories;
    public string ContributionCategoryName(Guid id) =>
        Account.FindContributionCategory(id)?.Name ?? "—";
    /// <summary>False when a contribution has no (or a since-deleted) income category — the UI shows a friendly default.</summary>
    public bool HasContributionCategory(Guid id) => Account.FindContributionCategory(id) is not null;
    public string? ContributionCategoryRemovalBlocker(Guid id) => Account.ContributionCategoryRemovalBlocker(id);

    /// <summary>This period's real member deposits (excludes the carryover sentinel), newest first.</summary>
    public IReadOnlyList<Contribution> ContributionsThisPeriod =>
        Period.Contributions.Where(c => c.MemberId != Period.CarryoverSource)
            .OrderByDescending(c => c.Date).ToList();

    public Contribution? FindContribution(Guid id) => Period.FindContribution(id);

    /// <summary>The most recently added member contribution this period, for the Home "edit last income" shortcut.
    /// List order ≈ entry order, so the last-added is taken (not the latest-dated). Null when none logged yet.</summary>
    public Contribution? LastContribution =>
        Enumerable.Reverse(Period.Contributions.ToList())
            .FirstOrDefault(c => c.MemberId != Period.CarryoverSource);

    // --- Commands ---------------------------------------------------------

    /// <summary>Whether a fund is currently synced to a bank account (its balance is externally authoritative).</summary>
    public bool FundIsSynced(Guid fundId) => _account?.Funds.FirstOrDefault(f => f.Id == fundId)?.IsSynced ?? false;

    /// <summary>The account's synced fund (the one mirroring the linked bank account), or empty if none is marked.
    /// Bank-imported records route here automatically. First synced fund wins if several are marked.</summary>
    public Guid SyncedFundId => _account?.Funds.FirstOrDefault(f => f.IsSynced)?.Id ?? Guid.Empty;
    public bool HasSyncedFund => SyncedFundId != Guid.Empty;
    public string SyncedFundName => HasSyncedFund ? FundName(SyncedFundId) : "";

    /// <summary>Funds the user may target manually (transfers/deposits/income) — synced funds are excluded;
    /// they're driven only by the bank import flow.</summary>
    public IReadOnlyList<Fund> SelectableFunds => Account.RootFunds.Where(f => !f.IsSynced && !f.IsArchived).ToList();

    /// <summary>Funds an expense may be logged against — like <see cref="SelectableFunds"/> but keeps synced funds in
    /// the list (at the end). A synced fund's balance mirrors the real bank and isn't debited by a logged expense, so
    /// a manual entry is safe: it records the spend for budgets/breakdown while the bank balance stays authoritative,
    /// and the import de-dup handles the case where the same transaction later syncs in.</summary>
    public IReadOnlyList<Fund> ExpensableFunds =>
        Account.RootFunds.Where(f => !f.IsArchived).OrderBy(f => f.IsSynced ? 1 : 0).ToList();

    /// <summary><paramref name="tripId"/> attaches the expense to a trip. The caller passes
    /// <see cref="ActiveTrip"/> while trip mode is on — a default the user can clear on the form, not a rule, since
    /// the weekly shop still happens on the day you fly home.</summary>
    public Task AddExpense(Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, bool onBehalfOfOtherAccount = false, Guid? tagId = null, Guid? tripId = null) =>
        ExecuteOptimisticAsync(() =>
        {
            var expense = new Expense(categoryId, Money(amount), date, CurrentMemberId, fundId, note, onBehalfOfOtherAccount: onBehalfOfOtherAccount);
            expense.SetFundSynced(FundIsSynced(fundId));
            expense.SetTag(tagId);
            expense.SetTrip(tripId);
            Period.AddExpense(expense);
            // F4: sweep the change into savings. The server runs the identical service on its side of this request,
            // so the optimistic paint matches what the refetch brings back.
            _roundUps.Sweep(Account, Period, expense.Amount, expense.Date);
        },
        id => api.AddExpenseAsync(id, new AddExpenseRequest(categoryId, amount, fundId, date, note, onBehalfOfOtherAccount, tagId, tripId)),
        refetchAfter: true);

    // Bank-confirm flows only — bank provenance (externalId + auto-filed badge) isn't in the command API yet.
    // TODO(cutover): fold into POST /expenses once AddExpenseRequest carries bankExternalId/autoFiled.
    private Task AddExpenseWithBankLink(Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date,
        string? bankExternalId, bool autoFiled)
    {
        var expense = new Expense(categoryId, Money(amount), date, CurrentMemberId, fundId, note);
        expense.SetFundSynced(FundIsSynced(fundId));   // synced funds aren't debited (real bank balance handles it)
        expense.SetBankLink(bankExternalId, autoFiled);
        Period.AddExpense(expense);
        return SaveAsync();
    }

    public async Task EditExpense(Guid expenseId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, Guid? tagId = null)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        await ExecuteOptimisticAsync(() =>
        {
            var edited = Period.EditExpense(expenseId, categoryId, Money(amount), fundId, note, date);
            edited.SetFundSynced(FundIsSynced(fundId));
            edited.SetBankLink(before?.BankExternalId, autoFiled: false);   // keep provenance, clear the auto-filed badge
            edited.SetTag(tagId);   // the edit UI always sends the desired tag (null clears it)
        },
        id => api.EditExpenseAsync(id, expenseId, new EditExpenseRequest(categoryId, amount, fundId, date, note, tagId)),
        refetchAfter: true);   // EditExpense is append-only (mints a new id) — reconcile to adopt the server's
        // Editing a settlement-destination expense mirrors the new amount back to the source expense.
        if (before is { IsSettlementDestination: true, SettlementId: { } sid, SettledFromAccountId: { } sourceAccount })
            await SyncSourceSettlementAmount(sourceAccount, sid, amount);
    }

    public async Task RemoveExpense(Guid expenseId)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        await ExecuteOptimisticAsync(() => Period.RemoveExpense(expenseId),
            id => api.RemoveExpenseAsync(id, expenseId), refetchAfter: false);
        // Removing one side of a settlement reverses the other: deleting the source drops the destination expense;
        // deleting the destination un-settles the source (restores its full amount).
        if (before is { IsSettlementSource: true, SettledToAccountId: { } destAccount, SettlementId: { } sid })
            await RemoveLinkedSettlementExpense(destAccount, sid);
        else if (before is { IsSettlementDestination: true, SettledFromAccountId: { } sourceAccount, SettlementId: { } sid2 })
            await SyncSourceSettlementAmount(sourceAccount, sid2, 0m);
    }

    /// <summary>Record a deposit for the signed-in user, classified by category and attributed to a fund.</summary>
    public Task RecordDeposit(Guid categoryId, Guid fundId, decimal amount, DateOnly date) =>
        ExecuteOptimisticAsync(() =>
        {
            var contribution = Period.Deposit(CurrentMemberId, Money(amount), categoryId, fundId, date);
            contribution.SetFundSynced(FundIsSynced(fundId));
        },
        id => api.AddDepositAsync(id, new AddDepositRequest(categoryId, fundId, amount, date)),
        refetchAfter: true);   // a fresh row mints an id (a merge reuses one) — reconcile to be safe

    /// <summary>Edit one of the signed-in user's own deposit rows.</summary>
    public Task EditDeposit(Guid contributionId, Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        EnsureOwnContribution(contributionId);   // friendlier local message; the server 403s regardless
        return ExecuteOptimisticAsync(() =>
        {
            Period.EditContribution(contributionId, Money(amount), categoryId, fundId, date);
            Period.FindContribution(contributionId)?.SetFundSynced(FundIsSynced(fundId));
        },
        id => api.EditDepositAsync(id, contributionId, new EditDepositRequest(categoryId, fundId, amount, date)),
        refetchAfter: true);
    }

    /// <summary>Remove one of the signed-in user's own deposit rows.</summary>
    public Task RemoveDeposit(Guid contributionId)
    {
        EnsureOwnContribution(contributionId);
        return ExecuteOptimisticAsync(() => Period.RemoveContribution(contributionId),
            id => api.RemoveDepositAsync(id, contributionId), refetchAfter: false);
    }

    // --- Recurring items (BACKLOG #13, phase 1) — expectations that post a real expense/deposit on confirm ------
    public IReadOnlyList<RecurringItem> RecurringItems => Account.RecurringItems;

    /// <summary>Recurring items due (and not yet handled) in the open period as of today — what the bell prompts for.</summary>
    public IReadOnlyList<RecurringItem> DueRecurring() =>
        IsPeriodOpen ? Account.RecurringItems.Where(r => r.IsDue(Period.From, Period.To, Today())).ToList() : [];

    /// <summary>Recurring items coming up within <paramref name="windowDays"/> days (pending, not yet due) — a heads-up.</summary>
    public IReadOnlyList<RecurringItem> UpcomingRecurring(int windowDays = 5) =>
        IsPeriodOpen ? Account.RecurringItems.Where(r => r.IsUpcoming(Period.From, Period.To, Today(), windowDays)).ToList() : [];

    /// <summary>How many days until a recurring item is due, within the current period.</summary>
    public int RecurringDaysUntilDue(RecurringItem r) => r.DaysUntilDue(Period.From, Period.To, Today());

    /// <summary>Total of the known-amount recurring <b>bills</b> still expected (unhandled) this period — money that's
    /// effectively already spoken for, even though it hasn't been logged yet. Reminder-only items are excluded (no
    /// predictable amount). Keeps "free to allocate" honest.</summary>
    public Money BillsDueThisPeriod =>
        !IsPeriodOpen ? Money(0m)
        : Money(Account.RecurringItems
            .Where(r => r.Kind == RecurringKind.Expense && r.HasKnownAmount && r.IsPending(Period.From, Period.To))
            .Sum(r => r.ExpectedAmount));

    public Task AddRecurring(string name, RecurringKind kind, RecurringAmountMode mode, decimal expected, int dayOfMonth, Guid categoryId, Guid fundId, string? icon, bool autoPost = false, Guid? linkedDebtBucketId = null) =>
        ExecuteOptimisticAsync(() =>
        {
            var item = new RecurringItem(name, kind, mode, expected, dayOfMonth, categoryId, fundId, icon, autoPost);
            item.SetCreatedOn(Today());
            item.SetLinkedDebtBucket(linkedDebtBucketId);
            SyncLoanDueDay(item);
            DefaultLoanToPaymentDriven(item, wasLinkedToSameBucket: false);
            Account.AddRecurring(item);
        },
        id => api.AddRecurringAsync(id, new AddRecurringRequest(name, RecurringKindString(kind), RecurringModeString(mode),
            expected, dayOfMonth, categoryId, fundId, icon, autoPost, linkedDebtBucketId)),
        refetchAfter: true);

    public Task UpdateRecurring(Guid id, string name, RecurringAmountMode mode, decimal expected, int dayOfMonth, Guid categoryId, Guid fundId, string? icon, bool autoPost = false, Guid? linkedDebtBucketId = null) =>
        ExecuteOptimisticAsync(() =>
        {
            var previousLink = Account.FindRecurring(id)?.LinkedDebtBucketId;
            Account.FindRecurring(id)?.Update(name, mode, expected, dayOfMonth, categoryId, fundId, icon, autoPost);
            Account.FindRecurring(id)?.SetLinkedDebtBucket(linkedDebtBucketId);   // authoritative: null unlinks
            if (Account.FindRecurring(id) is { } updated)
            {
                SyncLoanDueDay(updated);
                DefaultLoanToPaymentDriven(updated, previousLink == updated.LinkedDebtBucketId);
            }
        },
            acct => api.UpdateRecurringAsync(acct, id, new UpdateRecurringRequest(name, RecurringModeString(mode),
                expected, dayOfMonth, categoryId, fundId, icon, autoPost, linkedDebtBucketId)),
            refetchAfter: true);

    /// <summary>Mirror of the server's <c>RecurringMap.SyncLoanDueDay</c> for the optimistic local aggregate: a linked
    /// loan's stated installment day wins; when the loan has none, the bill's day fills it in. Without this the
    /// optimistic view would show a due date the refetch is about to correct.</summary>
    private void SyncLoanDueDay(RecurringItem item)
    {
        if (item.LinkedDebtBucketId is not { } bucketId) return;
        if (Account.FindSavingCategory(bucketId) is not { IsDebt: true } debt) return;
        if (debt.DebtInstallmentDay is { } loanDay) item.SetDayOfMonth(loanDay);
        else Account.SetSavingDebtInstallmentDay(bucketId, item.DayOfMonth);
    }

    /// <summary>Mirror of the server's <c>RecurringMap.DefaultLoanToPaymentDriven</c> — see there for why this fires
    /// only on the transition into a link and never on an ordinary re-save.</summary>
    private void DefaultLoanToPaymentDriven(RecurringItem item, bool wasLinkedToSameBucket)
    {
        if (wasLinkedToSameBucket) return;
        if (item.LinkedDebtBucketId is not { } bucketId) return;
        if (FindSavingBucket(bucketId) is not { IsDebt: true, DebtPaymentDriven: false }) return;
        Account.SetSavingDebtPaymentDriven(bucketId, true, Today());
    }

    /// <summary>Whether picking this loan in the bill editor will switch it onto logged payments — i.e. it isn't
    /// already following them, and this bill isn't already the one linked to it.</summary>
    public bool LinkingWouldSwitchLoanToPaymentDriven(Guid bucketId, Guid? currentlyLinkedBucketId) =>
        currentlyLinkedBucketId != bucketId
        && FindSavingBucket(bucketId) is { IsDebt: true, DebtPaymentDriven: false };

    /// <summary>The linked loan's installment day, when a bill's due date is being dictated by one.</summary>
    public int? LoanDueDayFor(Guid? linkedDebtBucketId) =>
        linkedDebtBucketId is { } id && FindSavingBucket(id) is { IsDebt: true } d ? d.DebtInstallmentDay : null;

    /// <summary>Whether an active recurring bill services this debt — i.e. something is set up to log its installments
    /// without the user remembering to. The difference between "the balance follows my logs" being a working
    /// arrangement and a promise nothing is helping them keep.</summary>
    public bool DebtHasLinkedBill(Guid bucketId) =>
        Account.RecurringItems.Any(r => r.Active && r.LinkedDebtBucketId == bucketId);

    /// <summary>The installment logged against this debt in the open period, if any — one group id, whatever the
    /// number of rows it posted. Null when this period's payment hasn't been recorded.</summary>
    public Guid? DebtInstallmentLoggedThisPeriod(Guid bucketId) =>
        IsPeriodOpen
            ? Period.Expenses.FirstOrDefault(e => e.DebtBucketId == bucketId && e.InstallmentGroupId is not null)?.InstallmentGroupId
            : null;

    /// <summary>Total actually paid toward this debt in the open period across every installment row (principal,
    /// interest and extras) — what the row's "logged" marker reports.</summary>
    public Money DebtPaidThisPeriod(Guid bucketId) =>
        Money(!IsPeriodOpen ? 0m
            : Period.Expenses.Where(e => e.DebtBucketId == bucketId && e.InstallmentGroupId is not null)
                .Sum(e => e.Amount.Amount));

    public Task RemoveRecurring(Guid id) =>
        ExecuteOptimisticAsync(() => Account.RemoveRecurring(id), acct => api.RemoveRecurringAsync(acct, id), refetchAfter: false);
    public Task SetRecurringActive(Guid id, bool active) =>
        ExecuteOptimisticAsync(() => Account.FindRecurring(id)?.SetActive(active), acct => api.SetRecurringActiveAsync(acct, id, active), refetchAfter: true);

    // The request DTOs carry kind/mode as language-independent strings (the server's RecurringMap parses them).
    private static string RecurringKindString(RecurringKind kind) => kind == RecurringKind.Income ? "income" : "expense";
    private static string RecurringModeString(RecurringAmountMode mode) => mode switch
    {
        RecurringAmountMode.Fixed => "fixed",
        RecurringAmountMode.Typical => "typical",
        _ => "reminder",
    };

    /// <summary>Confirm a due recurring item with the <b>real</b> amount: posts a normal expense/contribution, nudges a
    /// Typical estimate toward the actual, and marks it handled for this period — all in a single save.</summary>
    /// <param name="principalTagName">Localized fallback names for the split tags when the bill is debt-linked. The
    /// caller supplies them because only the UI knows the language; existing tags on the loan still win.</param>
    public Task ConfirmRecurring(Guid id, decimal actualAmount,
        string principalTagName = "Loan principal", string interestTagName = "Loan interest")
    {
        if (Account.FindRecurring(id) is null) return Task.CompletedTask;
        return ExecuteOptimisticAsync(() =>
        {
            var item = Account.FindRecurring(id)!;
            if (actualAmount > 0m) item.LearnFromActual(actualAmount);
            PostRecurringItem(item, actualAmount, principalTagName, interestTagName);
        },
        acct => api.ConfirmRecurringAsync(acct, id, actualAmount),
        refetchAfter: true);   // posts a real expense/income (mints an id) — reconcile
    }

    /// <summary>
    /// Post a due recurring item locally, routing a debt-linked bill through the installment split. Mirrors the
    /// server's <c>RecurringMap.Post</c> — both confirm and auto-post go through here so the optimistic render and
    /// the authoritative server result can't disagree about what a linked bill produces.
    /// </summary>
    private void PostRecurringItem(RecurringItem item, decimal amount, string principalTagName, string interestTagName)
    {
        var debt = item.LinkedDebtBucketId is { } bucketId ? FindSavingBucket(bucketId) : null;
        if (debt is not { IsDebt: true })
        {
            Period.PostRecurring(item, amount, CurrentMemberId, FundIsSynced(item.FundId));
            return;
        }
        // EnsureInstallmentTags prefers the tags this loan's earlier rows already carry, so the web and the server's
        // auto-post converge on one pair rather than creating a second set per language.
        var (principalTag, interestTag) = Account.EnsureInstallmentTags(debt.Id, principalTagName, interestTagName);
        Period.PostRecurring(item, amount, CurrentMemberId, FundIsSynced(item.FundId), debt, principalTag, interestTag);
    }

    /// <summary>Debt buckets a recurring bill can be linked to (active, non-archived) — the "this is a loan
    /// installment for…" picker.</summary>
    public IReadOnlyList<SavingCategory> LinkableDebts =>
        Account.SavingCategories.Where(s => s.IsDebt && !s.IsArchived).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // --- Statement file import (CSV/OFX/QIF → real expenses & income) ------

    /// <summary>Import chosen statement rows in one save: a negative amount becomes an expense, a positive one a
    /// contribution (income). Each row carries the category/fund the user picked in the review step.</summary>
    public async Task ImportTransactions(IReadOnlyList<(decimal Amount, DateOnly Date, Guid CategoryId, Guid FundId, string Note)> rows)
    {
        // Same skip rule the old local import applied; the server would also skip these, but a row with a category
        // the account no longer has would fail the whole batch there, so filter the obvious empties here.
        var dto = rows
            .Where(r => r.Amount != 0m && r.CategoryId != Guid.Empty && r.FundId != Guid.Empty)
            .Select(r => new ImportRowDto(r.Amount, r.Date, r.CategoryId, r.FundId, string.IsNullOrWhiteSpace(r.Note) ? null : r.Note))
            .ToList();
        if (dto.Count == 0) return;
        var accountId = CurrentAccountId;
        await _pushLock.WaitAsync();   // serialize with the deferred whole-snapshot PUT (see ExecuteAsync)
        try
        {
            await api.ImportTransactionsAsync(accountId, new ImportTransactionsRequest(dto));
            await RefreshFromServerAsync(accountId);
        }
        finally { _pushLock.Release(); }
    }

    /// <summary>Does an expense/contribution with this date and (absolute) amount already exist this period? Used to
    /// pre-flag likely duplicates when re-importing an overlapping statement, so nothing double-counts.</summary>
    public bool ImportLooksDuplicate(DateOnly date, decimal amount)
    {
        var abs = Math.Abs(amount);
        return amount < 0m
            ? Period.Expenses.Any(e => e.Date == date && e.Amount.Amount == abs)
            : Period.Contributions.Any(c => c.MemberId != FinApp.Domain.Periods.Period.CarryoverSource && c.Date == date && c.Paid.Amount == abs);
    }

    /// <summary>Whether a date falls inside the current open period (imports can only post to the active period).</summary>
    public bool ImportInPeriod(DateOnly date) => date >= Period.From && date <= Period.To;

    /// <summary>Skip a due recurring item this period (marks it handled without posting anything).</summary>
    public Task SkipRecurring(Guid id)
    {
        if (Account.FindRecurring(id) is null) return Task.CompletedTask;
        return ExecuteOptimisticAsync(() => Account.FindRecurring(id)?.MarkHandled(Period.From, skipped: true),
            acct => api.SkipRecurringAsync(acct, id), refetchAfter: true);
    }

    /// <summary>Undo a skip — the item falls due again this period. A no-op unless it really was skipped here, so a
    /// posted item can never be re-armed while its expense sits on the ledger.</summary>
    public Task UnskipRecurring(Guid id)
    {
        if (Account.FindRecurring(id) is not { } item || !item.SkippedIn(Period.From)) return Task.CompletedTask;
        return ExecuteOptimisticAsync(() => Account.FindRecurring(id)?.ClearHandled(),
            acct => api.UnskipRecurringAsync(acct, id), refetchAfter: true);
    }

    /// <summary>Recurring items deliberately skipped in the open period — offered an undo, and worth showing because
    /// a skip quietly changes "bills still due" and so the safe-to-spend figure that reads off it.</summary>
    public IReadOnlyList<FinApp.Domain.Recurring.RecurringItem> SkippedRecurring() =>
        IsPeriodOpen
            ? Account.RecurringItems.Where(r => r.Active && r.SkippedIn(Period.From)).ToList()
            : [];

    /// <summary>Auto-post every due Fixed item flagged for it, marking each handled — one confirm command per item
    /// (auto-post <i>is</i> confirm-at-the-expected-amount), then a single refresh. Guarded against re-entry: this
    /// runs after every render, and the items only read as handled once the refresh lands. Returns what it posted
    /// so the UI can show a "posted automatically" notice.</summary>
    private bool _autoPosting;

    public async Task<IReadOnlyList<(string Name, Money Amount, RecurringKind Kind)>> AutoPostDueRecurringAsync()
    {
        if (!IsPeriodOpen || _autoPosting) return [];
        var due = Account.RecurringItems.Where(r => r.AutoPost && r.IsDue(Period.From, Period.To, Today())).ToList();
        if (due.Count == 0) return [];
        _autoPosting = true;
        await _pushLock.WaitAsync();   // serialize with the deferred whole-snapshot PUT (see ExecuteAsync)
        try
        {
            var posted = new List<(string, Money, RecurringKind)>();
            var accountId = CurrentAccountId;
            foreach (var item in due)
            {
                await api.ConfirmRecurringAsync(accountId, item.Id, item.ExpectedAmount);
                posted.Add((item.Name, Money(item.ExpectedAmount), item.Kind));
            }
            await RefreshFromServerAsync(accountId);
            return posted;
        }
        finally { _pushLock.Release(); _autoPosting = false; }
    }

    /// <summary>Log per-fund reconciliation drift as recategorizable <b>Adjustment</b> entries in the current
    /// period so its books reconcile to reality: an <i>expense</i> where a fund holds less than the ledger expected,
    /// a money-in <i>deposit</i> where it holds more. Reuses (or creates once) an "Adjustment" category.</summary>
    public async Task RecordReconciliationAdjustments(IReadOnlyList<(Guid FundId, decimal Gap)> gaps, DateOnly date)
    {
        Guid? expenseCat = null, contribCat = null;
        foreach (var (fundId, gap) in gaps)
        {
            if (gap < 0)
            {
                expenseCat ??= AllCategories.FirstOrDefault(c => string.Equals(c.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))?.Id
                               ?? await AddCategory("Adjustment", null, "⚖️");
                await AddExpense(expenseCat.Value, Math.Abs(gap), fundId, "Reconciliation", date);
            }
            else if (gap > 0)
            {
                contribCat ??= ContributionCategories.FirstOrDefault(c => string.Equals(c.Name, "Adjustment", StringComparison.OrdinalIgnoreCase))?.Id
                               ?? await AddContributionCategory("Adjustment", "⚖️");
                await RecordDeposit(contribCat.Value, fundId, gap, date);
            }
        }
    }

    /// <summary>True when the deposit belongs to the signed-in user (only they may edit/remove it).</summary>
    public bool CanHandleContribution(Contribution c) => c.MemberId == CurrentMemberId;

    private void EnsureOwnContribution(Guid contributionId)
    {
        var c = Period.FindContribution(contributionId);
        if (c is null || !CanHandleContribution(c))
            throw new InvalidOperationException("You can only change your own contributions.");
    }

    public async Task<Guid> AddContributionCategory(string name, string? icon = null)
    {
        var result = await ExecuteOptimisticAsync(() =>
        {
            var c = Account.AddContributionCategory(name);
            Account.SetContributionCategoryIcon(c.Id, icon);
        },
        id => api.CreateContributionCategoryAsync(id, new CreateContributionCategoryRequest(name, icon)),
        refetchAfter: true);
        return result.EntityId ?? Guid.Empty;
    }

    public Task RenameContributionCategory(Guid id, string name)
    {
        var icon = ContributionCategoryStoredIcon(id);
        return ExecuteOptimisticAsync(() => Account.RenameContributionCategory(id, name),
            acct => api.EditContributionCategoryAsync(acct, id, new EditContributionCategoryRequest(name, icon)), refetchAfter: true);
    }

    /// <summary>Rename a contribution category and set its icon in one save.</summary>
    public Task SaveContributionCategory(Guid id, string name, string? icon) =>
        ExecuteOptimisticAsync(() =>
        {
            Account.RenameContributionCategory(id, name);
            Account.SetContributionCategoryIcon(id, icon);
        },
        acct => api.EditContributionCategoryAsync(acct, id, new EditContributionCategoryRequest(name, icon)),
        refetchAfter: true);

    public string ContributionCategoryIcon(Guid id) =>
        CategoryIcons.Effective(Account.FindContributionCategory(id)?.Icon, Account.FindContributionCategory(id)?.Name);
    public string? ContributionCategoryStoredIcon(Guid id) => Account.FindContributionCategory(id)?.Icon;

    public Task RemoveContributionCategory(Guid id) =>
        ExecuteOptimisticAsync(() => Account.RemoveContributionCategory(id),
            acct => api.RemoveContributionCategoryAsync(acct, id), refetchAfter: false);

    public Task AllocateSaving(Guid savingCategoryId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() => Period.AllocateToSavings(savingCategoryId, Money(amount), Today(), note, PriorSaved),
            id => api.AddSavingDepositAsync(id, new AddSavingDepositRequest(savingCategoryId, amount, Today(), note)), refetchAfter: true);

    public Task EditSavingDeposit(Guid allocationId, decimal amount) =>
        ExecuteOptimisticAsync(() => Period.EditSavingDeposit(allocationId, Money(amount), PriorSaved),
            id => api.EditSavingDepositAsync(id, allocationId, new EditSavingDepositRequest(amount)), refetchAfter: true);

    public Task RemoveSavingDeposit(Guid allocationId) =>
        ExecuteOptimisticAsync(() => Period.RemoveSavingAllocation(allocationId),
            id => api.RemoveSavingDepositAsync(id, allocationId), refetchAfter: false);

    // Empty FundId: the server derives the same default the web used (first spendable non-synced fund) — so the
    // optimistic apply resolves it locally the same way (DefaultFundId) to keep the instant paint faithful.
    public Task SpendFromSavings(Guid savingCategoryId, Guid categoryId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() => Period.ConvertSavingToExpense(savingCategoryId, categoryId, Money(amount), Today(), CurrentMemberId, DefaultFundId, note),
            id => api.SpendFromSavingsAsync(id, new SpendFromSavingsRequest(savingCategoryId, categoryId, amount, Today(), Guid.Empty, note)), refetchAfter: true);

    public Task ConvertSavingToBudget(Guid savingCategoryId, Guid categoryId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() => Period.ConvertSavingToBudget(savingCategoryId, categoryId, Money(amount), Today(), note),
            id => api.ConvertSavingToBudgetAsync(id, new ConvertSavingToBudgetRequest(savingCategoryId, categoryId, amount, Today(), note)), refetchAfter: true);

    /// <summary>Deploy a bucket to its goal (e.g. a loan prepayment) from a chosen fund: money leaves the account but
    /// it's not an expense and doesn't dent the savings figures. The fund is the one physically holding the money.
    /// On a debt bucket the server also records an extra principal payment.</summary>
    public Task DisburseSaving(Guid savingCategoryId, Guid fundId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() =>
        {
            var transfer = Period.DisburseSaving(savingCategoryId, fundId, Money(amount), Today(), note);
            transfer.SetFundSynced(FundIsSynced(fundId));
            Account.RecordSavingDebtPayment(savingCategoryId, amount, Today());
        },
        id => api.DisburseSavingAsync(id, new DisburseSavingRequest(savingCategoryId, fundId, amount, Today(), note)),
        refetchAfter: true);

    // --- Bucket lifecycle (archive a paid-off debt / reached goal) ---
    public bool SavingBucketIsArchived(Guid id) => FindSavingBucket(id)?.IsArchived ?? false;
    public bool SavingBucketIsDebtCleared(Guid id) => FindSavingBucket(id)?.IsDebtCleared ?? false;
    public Task SetSavingBucketArchived(Guid id, bool archived) =>
        ExecuteOptimisticAsync(() => Account.SetSavingArchived(id, archived),
            acct => api.SetSavingBucketArchivedAsync(acct, id, archived), refetchAfter: true);

    /// <summary>Move earmarked money from one savings bucket to another (net-neutral).</summary>
    public Task MoveSavingToBucket(Guid fromBucketId, Guid toBucketId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() => Period.TransferSavings(fromBucketId, toBucketId, Money(amount), Today(), note),
            id => api.MoveSavingsAsync(id, new MoveSavingsRequest(fromBucketId, toBucketId, amount, Today(), note)), refetchAfter: true);

    /// <summary>What the emergency fund should hold — 3× essential spending, rounded up to 500. Null when nothing is
    /// marked essential (there is nothing honest to derive it from).</summary>
    /// <summary>Closed periods that ran materially above this account's typical spend, worst first, with the
    /// category that drove each. Observation only — see <c>Account.CostHeavyPeriods</c> for why it doesn't predict.</summary>
    public IReadOnlyList<FinApp.Domain.Accounts.Account.CostHeavyPeriod> CostHeavyPeriods() => Account.CostHeavyPeriods();

    /// <summary>The typical (median) closed-period spend these are measured against. Null until there's enough history.</summary>
    public decimal? TypicalPeriodSpend() => Account.TypicalPeriodSpend();

    public decimal? EmergencyTarget() => Account.EmergencyFundTarget();

    /// <summary>The essential spend the target was derived from. Read this for the basis rather than dividing the
    /// target — it is rounded up, so the division reports a figure nobody spent.</summary>
    public decimal? EssentialSpendPerPeriod() => Account.EssentialSpendPerPeriod();

    /// <summary>The bucket already holding the emergency label, when it isn't <paramref name="excludingId"/> — so the
    /// editor can say the label is about to move rather than silently taking it.</summary>
    public string? OtherEmergencyFundName(Guid excludingId) =>
        Account.EmergencyFund is { } e && e.Id != excludingId ? e.Name : null;

    public bool SavingBucketIsEmergency(Guid id) => FindSavingBucket(id)?.IsEmergencyFund ?? false;

    /// <summary>True during initial setup (only the first period exists) — when a bucket's pre-existing initial balance may be set.</summary>
    public bool CanSetInitialSavings => PeriodCount == 1;

    // Saving bucket CRUD — one 18-field upsert request, applied server-side by SavingBucketConfig.Apply (the same
    // priority order the web modal used: debt → investment → ordinary goal; a sinking fund clears any goal).
    public async Task<Guid> AddSavingBucket(string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null,
        bool isDebt = false, decimal debtBalance = 0m, decimal debtRate = 0m, decimal debtInstallment = 0m, decimal? plannedContribution = null,
        bool isInvestment = false, decimal invRate = 0m, decimal invTermYears = 0m, int invCompounds = 12,
        Guid? fundId = null, IEnumerable<PlannedCost>? costs = null, bool isExpensesFund = false,
        decimal? debtOriginalBalance = null, int? debtInstallmentDay = null, DateOnly? debtStartDate = null,
        bool debtPaymentDriven = false, bool isEmergencyFund = false, decimal debtResidual = 0m)
    {
        var req = BuildBucketRequest(name, goalAmount, thresholdPercent, notifyOnMilestone, initialAmount, icon,
            isDebt, debtBalance, debtRate, debtInstallment, plannedContribution,
            isInvestment, invRate, invTermYears, invCompounds, fundId, costs, isExpensesFund,
            debtOriginalBalance, debtInstallmentDay, debtStartDate, debtPaymentDriven, isEmergencyFund, debtResidual);
        var result = await ExecuteAsync(id => api.AddSavingBucketAsync(id, req));
        return result.EntityId ?? Guid.Empty;
    }

    public Task SaveSavingBucket(Guid savingCategoryId, string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null,
        bool isDebt = false, decimal debtBalance = 0m, decimal debtRate = 0m, decimal debtInstallment = 0m, decimal? plannedContribution = null,
        bool isInvestment = false, decimal invRate = 0m, decimal invTermYears = 0m, int invCompounds = 12,
        Guid? fundId = null, IEnumerable<PlannedCost>? costs = null, bool isExpensesFund = false,
        decimal? debtOriginalBalance = null, int? debtInstallmentDay = null, DateOnly? debtStartDate = null,
        bool debtPaymentDriven = false, bool isEmergencyFund = false, decimal debtResidual = 0m)
    {
        var req = BuildBucketRequest(name, goalAmount, thresholdPercent, notifyOnMilestone, initialAmount, icon,
            isDebt, debtBalance, debtRate, debtInstallment, plannedContribution,
            isInvestment, invRate, invTermYears, invCompounds, fundId, costs, isExpensesFund,
            debtOriginalBalance, debtInstallmentDay, debtStartDate, debtPaymentDriven, isEmergencyFund, debtResidual);
        return ExecuteAsync(id => api.SaveSavingBucketAsync(id, savingCategoryId, req));
    }

    private static SaveSavingBucketRequest BuildBucketRequest(string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon,
        bool isDebt, decimal debtBalance, decimal debtRate, decimal debtInstallment, decimal? plannedContribution,
        bool isInvestment, decimal invRate, decimal invTermYears, int invCompounds,
        Guid? fundId, IEnumerable<PlannedCost>? costs, bool isExpensesFund,
        decimal? debtOriginalBalance = null, int? debtInstallmentDay = null, DateOnly? debtStartDate = null,
        bool debtPaymentDriven = false, bool isEmergencyFund = false, decimal debtResidual = 0m) =>
        new(name, icon, goalAmount, thresholdPercent, notifyOnMilestone, initialAmount,
            isDebt, debtBalance, debtRate, debtInstallment,
            DebtOriginalBalance: debtOriginalBalance, DebtInstallmentDay: debtInstallmentDay, DebtStartDate: debtStartDate,
            PlannedContribution: plannedContribution,
            IsInvestment: isInvestment, InvRate: invRate, InvTermYears: invTermYears, InvCompounds: invCompounds, FundId: fundId,
            Costs: costs?.Select(c => new PlannedCostDto(c.Label, c.Amount, CadenceString(c.Cadence), c.DueDate)).ToList(),
            IsExpensesFund: isExpensesFund,
            DebtPaymentDriven: debtPaymentDriven, IsEmergencyFund: isEmergencyFund, DebtResidual: debtResidual);

    private static string CadenceString(CostCadence cadence) => cadence switch
    {
        CostCadence.Monthly => "monthly",
        CostCadence.Quarterly => "quarterly",
        CostCadence.Yearly => "yearly",
        _ => "one-off",
    };

    // --- Expenses fund (sinking-fund cost list) -------------------------------------------------------

    /// <summary>The flat amount to set aside into this bucket per period to cover all its listed future costs, or null
    /// when it has no cost list. This is the sinking-fund average (recurring costs annualised, a dated one-off spread
    /// across the months until due) <b>net of what the bucket already holds</b>, so a part-funded one-off stops asking
    /// for money you've got. A suggestion only — nothing is reserved automatically.</summary>
    public decimal? BucketMonthlySetAside(Guid bucketId)
    {
        var b = FindSavingBucket(bucketId);
        if (b is null || !b.HasCosts) return null;
        return b.MonthlySetAside(Period.From, SavingBucketSaved(bucketId).Amount);
    }

    /// <summary>
    /// Sinking-fund buckets that haven't had their monthly set-aside put in yet <b>this period</b>, with the amount
    /// still owed. Ordered biggest gap first. Empty once each bucket has had its set-aside topped up.
    /// <para>
    /// Only allocations made in the current period count, which is the point: a sinking fund is a standing monthly
    /// commitment, so last month's contribution doesn't cover this month. Money moved <i>out</i> of the bucket this
    /// period nets off, so a top-up followed by a withdrawal correctly still reads as owed.
    /// </para>
    /// </summary>
    public IReadOnlyList<(SavingCategory Bucket, decimal Owed)> SinkingFundsShortThisPeriod()
    {
        var result = new List<(SavingCategory, decimal)>();
        foreach (var (bucket, _) in SavingBuckets)
        {
            if (bucket.IsArchived || !bucket.HasCosts) continue;
            if (BucketMonthlySetAside(bucket.Id) is not { } need || need <= 0m) continue;

            var putInThisPeriod = Period.SavingAllocations
                .Where(a => a.SavingCategoryId == bucket.Id)
                .Sum(a => a.Amount.Amount);

            var owed = decimal.Round(need - putInThisPeriod, 2);
            if (owed > 0m) result.Add((bucket, owed));
        }
        return result.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>Every live bucket's monthly set-aside added up — what the sinking funds jointly claim each month.
    /// Archived buckets are excluded; they aren't being funded.</summary>
    public decimal TotalMonthlySetAside =>
        SavingBuckets.Where(b => !b.Bucket.IsArchived)
                     .Sum(b => BucketMonthlySetAside(b.Bucket.Id) ?? 0m);

    /// <summary>
    /// The cash runway: where the balance lands over the next <paramref name="months"/> months. Returns <b>null</b>
    /// when there's no trustworthy basis to project from — no completed period to average and nothing recurring
    /// declared — because a projection with no income signal reports certain ruin for anyone who simply hasn't
    /// filled that in yet.
    /// <para>
    /// <b>Demonstrated history wins over declarations.</b> Averaging completed periods reflects what actually happens,
    /// including the income and spending a user never declared as recurring. Recurring items are the fallback for a
    /// young account with no history to average — the same "demonstrated beats planned" choice the savings pace makes.
    /// </para>
    /// <para>
    /// <paramref name="openingBalance"/> is passed in rather than read from <see cref="ClosingBalance"/> so the caller
    /// can hand over the <b>same figure it is displaying</b> (which may carry a live bank adjustment). A runway whose
    /// first month disagrees with the balance shown right above it is worse than no runway.
    /// </para>
    /// </summary>
    public CashFlowProjection? ProjectCashFlow(Money openingBalance, int months = 6)
        => ProjectCashFlow(openingBalance, 0m, months);

    /// <summary>
    /// The runway projection, optionally with monthly spending nudged by <paramref name="spendingDelta"/> — the
    /// "what if I spent differently?" slider. delta 0 is the plain runway shown on Home; a negative delta models
    /// spending less. Reuses the same demonstrated/recurring base figures as the plain runway so the two always agree.
    /// </summary>
    public CashFlowProjection? ProjectCashFlow(Money openingBalance, decimal spendingDelta, int months = 6)
    {
        if (CashFlowBase() is not { } b) return null;   // no history and nothing declared — say nothing
        return CashFlowForecast.Project(
            openingBalance.Amount, b.Income, b.Spending + spendingDelta, Period.From, months,
            b.Basis, TotalMonthlySetAside, b.HasUnknown);
    }

    /// <summary>The income/spending figures the runway rests on: an average of completed periods when there's history
    /// (the honest basis), else the declared recurring items. Null when there's neither. Kept in one place so the
    /// plain runway and the what-if slider can never diverge.</summary>
    private (decimal Income, decimal Spending, CashFlowBasis Basis, bool HasUnknown)? CashFlowBase()
    {
        if (CashFlowHistory.Demonstrated(Account.Periods) is { } seen)
            return (seen.Income, seen.Spending, CashFlowBasis.Demonstrated, false);

        var active = RecurringItems.Where(r => r.Active).ToList();
        var counted = active.Where(r => r.HasKnownAmount).ToList();
        if (counted.Count == 0) return null;

        return (
            counted.Where(r => r.Kind == RecurringKind.Income).Sum(r => r.ExpectedAmount),
            counted.Where(r => r.Kind == RecurringKind.Expense).Sum(r => r.ExpectedAmount),
            CashFlowBasis.Recurring,
            active.Any(r => !r.HasKnownAmount));
    }

    /// <summary>What this bucket's dated one-offs still need beyond what it holds — the "you're €X short" read, or null
    /// when there's nothing outstanding. Recurring costs are excluded: they're a rate that never completes.</summary>
    public decimal? BucketTargetShortfall(Guid bucketId)
    {
        var b = FindSavingBucket(bucketId);
        if (b is null || !b.HasCosts) return null;
        var short_ = b.TargetShortfall(SavingBucketSaved(bucketId).Amount);
        return short_ > 0m ? short_ : null;
    }

    public Guid? SavingBucketFundId(Guid id) => FindSavingBucket(id)?.FundId;

    /// <summary>The bucket's list of planned future costs (the sinking-fund lines), or empty when it has none.</summary>
    public IReadOnlyList<PlannedCost> SavingBucketCosts(Guid id) => FindSavingBucket(id)?.Costs ?? [];

    /// <summary>Persist a changed cost list on an existing sinking fund, preserving its name/icon/fund/starting balance —
    /// so costs can be added/edited/removed from the bucket itself (the inline "Add a cost" flow) rather than the edit
    /// modal. Re-uses <see cref="SaveSavingBucket"/>, exactly as the modal's Save does, just with a new list.</summary>
    public Task SaveSavingBucketCosts(Guid bucketId, IReadOnlyList<PlannedCost> costs)
    {
        var b = FindSavingBucket(bucketId);
        if (b is null) return Task.CompletedTask;
        return SaveSavingBucket(bucketId, b.Name, null, b.AlertThreshold * 100m, b.NotifyOnMilestone, b.InitialAmount,
            b.Icon, false, 0m, 0m, 0m, null, false, 0m, 0m, 12, b.FundId, costs, isExpensesFund: true);
    }

    /// <summary>Debt-payoff buckets vs ordinary savings buckets (each with its accumulated total), for the two
    /// Savings-tab sections. Reads the same <see cref="SavingBuckets"/> data — purely a split by kind.</summary>
    public bool SavingBucketIsDebt(Guid id) => FindSavingBucket(id)?.IsDebt ?? false;

    /// <summary>What's owed <b>today</b> — the anchored balance walked forward over the installments due since (see
    /// <see cref="SavingCategory.DebtBalanceOn"/>). Unanchored/legacy buckets return their stored balance unchanged.</summary>
    public decimal SavingBucketDebtBalance(Guid id) => FindSavingBucket(id)?.DebtBalanceOn(Today()) ?? 0m;
    public decimal SavingBucketDebtRate(Guid id) => FindSavingBucket(id)?.DebtAnnualRatePercent ?? 0m;
    public decimal SavingBucketDebtInstallment(Guid id) => FindSavingBucket(id)?.DebtInstallment ?? 0m;
    /// <summary>The date the debt's balance was last known true, or null when it isn't on a schedule.</summary>
    public DateOnly? SavingBucketDebtAsOf(Guid id) => FindSavingBucket(id)?.DebtBalanceAsOf;

    // --- R1 informative debt: interest read-outs + due-day + origination date ---
    /// <summary>Interest paid to date on a debt bucket — exact when its origination date is known, else estimated.</summary>
    public decimal SavingBucketDebtPaidInterest(Guid id) => FindSavingBucket(id)?.PaidInterestSoFar(Today()) ?? 0m;
    /// <summary>Interest still to pay from today until the debt clears at its current installment (0 when it never clears).</summary>
    public decimal SavingBucketDebtRemainingInterest(Guid id) => FindSavingBucket(id)?.RemainingInterest(Today()) ?? 0m;
    /// <summary>Whether the paid-interest figure is an estimate (no origination date recorded) — the UI labels it.</summary>
    public bool SavingBucketDebtPaidInterestEstimated(Guid id) => FindSavingBucket(id)?.DebtPaidInterestIsEstimate ?? false;
    /// <summary>The installment due-day (1–31), or null when unknown.</summary>
    public int? SavingBucketDebtInstallmentDay(Guid id) => FindSavingBucket(id)?.DebtInstallmentDay;
    /// <summary>The loan's origination date, or null when unrecorded (paid-interest is then estimated).</summary>
    public DateOnly? SavingBucketDebtStartDate(Guid id) => FindSavingBucket(id)?.DebtStartDate;

    // --- R2 installment split: payment-driven balances + logging a payment ---

    /// <summary>Whether this debt's balance moves only when an installment is logged here (rather than being walked
    /// forward over its schedule). Drives the "Log installment" action and the row's "you're tracking payments" note.</summary>
    public bool SavingBucketDebtPaymentDriven(Guid id) => FindSavingBucket(id)?.DebtPaymentDriven ?? false;

    /// <summary>A lease's residual/balloon — the sum its schedule amortises down to. 0 for an ordinary loan.</summary>
    public decimal SavingBucketDebtResidual(Guid id) => FindSavingBucket(id)?.DebtResidual ?? 0m;

    /// <summary>How a payment of <paramref name="total"/> against this debt splits today: what the extra lines take,
    /// then interest on what's owed, then principal. Pure preview — computed exactly as
    /// <c>Period.LogInstallment</c> will compute it, so the modal can't promise a split the post won't produce.</summary>
    public (decimal Interest, decimal Principal, decimal Extras) InstallmentSplit(Guid bucketId, decimal total, decimal extrasTotal, DateOnly? on = null)
    {
        var bucket = FindSavingBucket(bucketId);
        if (bucket is null || !bucket.IsDebt) return (0m, 0m, extrasTotal);
        var date = on ?? Today();
        var servicing = Math.Max(0m, total - extrasTotal);
        var interest = Math.Min(servicing,
            FinApp.Forecasting.LoanForecast.MonthlyInterest(bucket.DebtBalanceOn(date), bucket.DebtAnnualRatePercent));
        return (interest, servicing - interest, extrasTotal);
    }

    /// <summary>Log a loan installment: posts the principal, interest and any additional rows as one linked group, and
    /// (payment-driven buckets only) takes the principal off the balance. Refetches — the post mints several ids and,
    /// on a payment-driven bucket, moves the debt figure every other panel reads.</summary>
    public Task LogInstallment(Guid bucketId, decimal total, Guid fundId, DateOnly date,
        Guid principalCategoryId, Guid interestCategoryId, IReadOnlyList<InstallmentExtra>? additional = null,
        Guid? principalTagId = null, Guid? interestTagId = null, string? note = null)
    {
        var bucket = FindSavingBucket(bucketId) ?? throw new InvalidOperationException("Savings bucket not found.");
        return ExecuteOptimisticAsync(
            () => Period.LogInstallment(bucket, Money(total), date, CurrentMemberId, fundId,
                principalCategoryId, interestCategoryId, additional, principalTagId, interestTagId, note,
                FundIsSynced(fundId)),
            id => api.LogInstallmentAsync(id, new LogInstallmentRequest(bucketId, total, fundId, date,
                principalCategoryId, interestCategoryId,
                additional?.Select(x => new InstallmentExtraDto(x.Amount.Amount, x.CategoryId, x.TagId, x.Note)).ToList(),
                principalTagId, interestTagId, note)),
            refetchAfter: true);
    }

    /// <summary>Remove a whole logged installment (every row of it), restoring the balance on a payment-driven bucket.</summary>
    public Task RemoveInstallment(Guid groupId)
    {
        var bucketId = Period.InstallmentGroup(groupId).FirstOrDefault()?.DebtBucketId;
        var bucket = bucketId is { } bid ? FindSavingBucket(bid) : null;
        return ExecuteOptimisticAsync(() => Period.RemoveInstallmentGroup(groupId, bucket),
            id => api.RemoveInstallmentAsync(id, groupId), refetchAfter: true);
    }

    /// <summary>The budget category this loan's installment rows were last filed under, across every period — so a
    /// repeat payment defaults to what the user already chose. Null when the loan has never been logged here.</summary>
    public Guid? LastInstallmentCategory(Guid bucketId) =>
        Account.Periods.SelectMany(p => p.Expenses)
            .Where(e => e.DebtBucketId == bucketId && e.Part != FinApp.Domain.Budgeting.InstallmentPart.Additional)
            .OrderByDescending(e => e.Date)
            .Select(e => (Guid?)e.CategoryId)
            .FirstOrDefault();

    /// <summary>This period's logged installments, newest first: the group id, the debt it serviced, the date, and the
    /// rows that make it up (so the ledger can show one payment rather than three unexplained expenses).</summary>
    public IReadOnlyList<(Guid GroupId, Guid? BucketId, DateOnly Date, IReadOnlyList<Expense> Rows)> InstallmentGroups() =>
        Period.Expenses.Where(e => e.InstallmentGroupId is not null)
            .GroupBy(e => e.InstallmentGroupId!.Value)
            .Select(g => (GroupId: g.Key, BucketId: g.First().DebtBucketId, Date: g.First().Date, Rows: (IReadOnlyList<Expense>)g.ToList()))
            .OrderByDescending(g => g.Date)
            .ToList();

    // --- Progress over time (#7): the original owed vs what's left, and how much has been cleared ---
    /// <summary>Debt buckets: the balance owed when the debt was first set up (the "€Y" in "paid off €X of €Y").</summary>
    public decimal SavingBucketDebtOriginal(Guid id) => FindSavingBucket(id)?.DebtOriginalBalance ?? 0m;
    /// <summary>Debt buckets: how much of the original balance has been paid off as of today — measured against the
    /// scheduled balance, so progress moves with the loan rather than only when a payment is recorded here.</summary>
    public decimal SavingBucketDebtPaidOff(Guid id) => FindSavingBucket(id)?.DebtPaidOffOn(Today()) ?? 0m;
    /// <summary>Debt buckets: fraction (0..1) of the original balance paid off, or null when there's no baseline.</summary>
    public decimal? SavingBucketDebtProgress(Guid id) => FindSavingBucket(id)?.DebtProgressRatioOn(Today());

    /// <summary>User-set planned per-period contribution to a bucket (#8), or null when pace is inferred from history.</summary>
    public decimal? SavingBucketPlannedContribution(Guid id) => FindSavingBucket(id)?.PlannedContribution;

    // --- Investment buckets (compound-growth projection) ---
    public bool SavingBucketIsInvestment(Guid id) => FindSavingBucket(id)?.IsInvestment ?? false;
    public decimal SavingBucketInvestmentRate(Guid id) => FindSavingBucket(id)?.InvestmentAnnualRatePercent ?? 0m;
    public decimal SavingBucketInvestmentTermYears(Guid id) => FindSavingBucket(id)?.InvestmentTermYears ?? 0m;
    public int SavingBucketInvestmentCompounds(Guid id) => FindSavingBucket(id)?.InvestmentCompoundsPerYear ?? 12;

    /// <summary>Project an investment bucket's future value — present value is its accumulated balance, adding
    /// <paramref name="extraPerMonth"/> each month over its term at its rate/compounding. Null when it isn't an investment.</summary>
    public FinApp.Forecasting.InvestmentForecast.Projection? ProjectInvestment(Guid id, decimal extraPerMonth)
    {
        var bucket = FindSavingBucket(id);
        if (bucket is null || !bucket.IsInvestment) return null;
        return FinApp.Forecasting.InvestmentForecast.Project(
            SavingBucketSaved(id).Amount, bucket.InvestmentAnnualRatePercent, bucket.InvestmentTermYears, bucket.InvestmentCompoundsPerYear, extraPerMonth);
    }

    // --- Forecasting projections (read-only; never touch the money model) ---
    /// <summary>Average amount added to a bucket per active period — the demonstrated saving pace, for projections.</summary>
    public Money? SavingBucketPace(Guid id) => _savings.AverageDepositPace(Account, id);

    /// <summary>The pace projections should use: the demonstrated pace from deposit history. Null only when there are
    /// no deposits yet. (The old per-bucket "planned contribution" override was removed — for debt the installment is
    /// the planned amount, and every projection modal lets you drag the pace to explore "what if I keep this up".)</summary>
    public Money? EffectiveSavingPace(Guid id) => SavingBucketPace(id);

    /// <summary>Debt buckets: the shrinking remaining-balance series (original owed → balance after each paying period),
    /// for a sparkline (#7). Fewer than 2 points means there's nothing to draw yet.</summary>
    public IReadOnlyList<decimal> SavingBucketDebtHistory(Guid id) => _savings.DebtBalanceHistory(Account, id);

    /// <summary>Debt buckets: projected whole months saved versus paying only the contractual installment, at the
    /// bucket's effective pace (planned contribution, or demonstrated pace). Null when there's no meaningful speed-up.</summary>
    public int? SavingBucketMonthsAhead(Guid id)
    {
        var bucket = FindSavingBucket(id);
        if (bucket is null || !bucket.IsDebt || bucket.DebtOriginalBalance <= 0m) return null;
        var pace = EffectiveSavingPace(id)?.Amount ?? 0m;
        var extra = pace - bucket.DebtInstallment;
        if (extra <= 0m) return null;
        var sim = FinApp.Forecasting.LoanForecast.SimulateExtra(
            bucket.DebtOriginalBalance, bucket.DebtAnnualRatePercent, bucket.DebtInstallment, extra);
        return sim is { MonthsSaved: > 0 } s ? s.MonthsSaved : null;
    }

    /// <summary>How much is currently set aside in a bucket (its accumulated balance).</summary>
    public Money SavingBucketSaved(Guid id)
    {
        var found = SavingBuckets.FirstOrDefault(x => x.Bucket.Id == id);
        return found.Bucket is null ? Money(0m) : found.Total;
    }

    /// <summary>Debt buckets mapped to loan-forecast inputs (balance/rate/installment) for the multi-debt planner.</summary>
    public IReadOnlyList<FinApp.Forecasting.LoanForecast.LoanInput> DebtLoanInputs =>
        SavingBuckets.Where(x => x.Bucket.IsDebt && !x.Bucket.IsArchived && x.Bucket.DebtBalance > 0m)
            .Select(x => new FinApp.Forecasting.LoanForecast.LoanInput(
                x.Bucket.Id, x.Bucket.Name, x.Bucket.DebtBalance, x.Bucket.DebtAnnualRatePercent, x.Bucket.DebtInstallment))
            .ToList();

    public string SavingBucketIcon(Guid id) =>
        CategoryIcons.Effective(FindSavingBucket(id)?.Icon, FindSavingBucket(id)?.Name);
    public string? SavingBucketStoredIcon(Guid id) => FindSavingBucket(id)?.Icon;

    public decimal SavingInitialAmount(Guid savingCategoryId) => FindSavingBucket(savingCategoryId)?.InitialAmount ?? 0m;

    public Task RemoveSavingBucket(Guid savingCategoryId) =>
        ExecuteOptimisticAsync(() => Account.RemoveSavingCategory(savingCategoryId),
            id => api.RemoveSavingBucketAsync(id, savingCategoryId), refetchAfter: false);

    // Fund CRUD + transfers
    public async Task<Guid> AddFund(string name, string? note = null, string? icon = null)
    {
        var result = await ExecuteOptimisticAsync(() =>
        {
            var fund = Account.AddFund(name);
            if (!string.IsNullOrWhiteSpace(note)) Account.SetFundNote(fund.Id, note);
            Account.SetFundIcon(fund.Id, icon);
        },
        id => api.CreateFundAsync(id, new CreateFundRequest(name, null, string.IsNullOrWhiteSpace(note) ? null : note, icon)),
        refetchAfter: true);
        return result.EntityId ?? Guid.Empty;
    }

    // The fund edit endpoint takes the full (name, note, icon) triple, so single-field setters pass the current
    // values of the other two along (captured before the optimistic apply changes them).
    public Task RenameFund(Guid fundId, string name)
    {
        var (note, icon) = (FundNote(fundId), FundStoredIcon(fundId));
        return ExecuteOptimisticAsync(() => Account.RenameFund(fundId, name),
            id => api.EditFundAsync(id, fundId, new EditFundRequest(name, note, icon)), refetchAfter: true);
    }

    public Task SetFundIcon(Guid fundId, string? icon)
    {
        var (name, note) = (FundName(fundId), FundNote(fundId));
        return ExecuteOptimisticAsync(() => Account.SetFundIcon(fundId, icon),
            id => api.EditFundAsync(id, fundId, new EditFundRequest(name, note, icon)), refetchAfter: true);
    }

    /// <summary>Toggle a fund's bank-synced flag (forward-only — see <see cref="Fund.IsSynced"/>).</summary>
    // TODO(cutover): needs a command endpoint (fund synced flag) — still local-mutate + whole-snapshot push.
    public Task SetFundSynced(Guid fundId, bool synced)
    {
        Account.SetFundSynced(fundId, synced);
        return SaveAsync();
    }

    /// <summary>Bind a fund to a specific bank account on the connection (the synced fund + which bank account it
    /// mirrors). Exactly one fund can be bound, so any other synced fund is cleared.</summary>
    public async Task BindFundToBank(Guid fundId, string? bankAccountRef = null)
    {
        foreach (var f in Account.Funds.Where(f => f.IsSynced && f.Id != fundId).ToList())
            Account.SetFundSynced(f.Id, false);
        Account.SetFundSynced(fundId, true);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, fundId);
        if (!string.IsNullOrEmpty(bankAccountRef))
            await api.SelectBankAccountAsync(CurrentAccountId, bankAccountRef);   // map the fund to this bank account
    }

    /// <summary>Unbind a fund from the bank connection (stops routing imports; existing entries keep their markers).</summary>
    public async Task UnbindFundFromBank(Guid fundId)
    {
        Account.SetFundSynced(fundId, false);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, null);
        try { await api.RecordConsentAsync("bank_sync", CurrentAccountId, granted: false); } catch { /* audit best-effort */ }
    }

    public string FundIcon(Guid fundId) =>
        CategoryIcons.Effective(Account.FindFund(fundId)?.Icon, Account.FindFund(fundId)?.Name);
    public string? FundStoredIcon(Guid fundId) => Account.FindFund(fundId)?.Icon;

    public Task SetFundNote(Guid fundId, string? note)
    {
        var (name, icon) = (FundName(fundId), FundStoredIcon(fundId));
        return ExecuteOptimisticAsync(() => Account.SetFundNote(fundId, note),
            id => api.EditFundAsync(id, fundId, new EditFundRequest(name, note, icon)), refetchAfter: true);
    }

    public bool FundHasOpeningBalance(Guid fundId) => Account.FundHasOpeningBalance(fundId);

    public Task RemoveFund(Guid fundId, Guid? moveOpeningBalancesTo = null) =>
        ExecuteOptimisticAsync(() => Account.RemoveFund(fundId, moveOpeningBalancesTo),
            id => api.RemoveFundAsync(id, fundId, moveOpeningBalancesTo), refetchAfter: false);

    /// <summary>Archive a fund (hide it, keep its history). If it still holds a balance, pass a <paramref name="moveBalanceTo"/>
    /// fund + <paramref name="amount"/> to move that money out first as a real transfer, so the account total is preserved
    /// and the archived fund is left at zero. No transaction is reassigned or deleted. Two commands now (the old path was
    /// one save): if the archive half fails the transfer stands — visible and re-doable, not money lost.</summary>
    public async Task ArchiveFund(Guid fundId, Guid? moveBalanceTo, decimal amount)
    {
        if (moveBalanceTo is { } target && target != Guid.Empty && amount > 0m)
            await ExecuteOptimisticAsync(() =>
            {
                var transfer = Period.TransferFunds(fundId, target, Money(amount), Today(), null);
                transfer.SetSyncedSides(FundIsSynced(fundId), FundIsSynced(target));
            },
            id => api.TransferFundsAsync(id, new TransferFundsRequest(fundId, target, amount, Today())),
            refetchAfter: true);
        await ExecuteOptimisticAsync(() => Account.SetFundArchived(fundId, true),
            id => api.SetFundArchivedAsync(id, fundId, true), refetchAfter: true);
    }

    public Task RestoreFund(Guid fundId) =>
        ExecuteOptimisticAsync(() => Account.SetFundArchived(fundId, false),
            id => api.SetFundArchivedAsync(id, fundId, false), refetchAfter: true);

    public Task SetFundOpeningBalance(Guid fundId, decimal amount) =>
        ExecuteOptimisticAsync(() => Period.SetInitialBalance(fundId, Money(amount)),
            id => api.SetFundOpeningBalanceAsync(id, fundId, amount), refetchAfter: true);

    public Task TransferFunds(Guid fromFundId, Guid toFundId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() =>
        {
            var transfer = Period.TransferFunds(fromFundId, toFundId, Money(amount), Today(), note);
            transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));
        },
        id => api.TransferFundsAsync(id, new TransferFundsRequest(fromFundId, toFundId, amount, Today(), note)),
        refetchAfter: true);

    public FundTransfer? FindFundTransfer(Guid id) => Period.FundTransfers.FirstOrDefault(t => t.Id == id);

    /// <summary>The most recent wallet-to-wallet transfer this period — what the Transfer modal's "Edit last"
    /// edits. Account-to-account transfers are deliberately excluded: they have two halves and a two-sided editor
    /// of their own, so editing one from here would touch only the near side.</summary>
    public FundTransfer? LastFundTransfer => Period.FundTransfers.OrderByDescending(t => t.Date).FirstOrDefault();

    public Task EditFundTransfer(Guid id, Guid fromFundId, Guid toFundId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() =>
        {
            var before = FindFundTransfer(id);
            var transfer = Period.EditFundTransfer(id, fromFundId, toFundId, Money(amount), note);
            transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));
            transfer.SetBankLink(before?.BankExternalId, autoFiled: false);
        },
        acct => api.EditFundTransferAsync(acct, id, new EditFundTransferRequest(fromFundId, toFundId, amount, note)),
        refetchAfter: true);   // EditFundTransfer is append-only (mints a new id)

    public Task RemoveFundTransfer(Guid id) =>
        ExecuteOptimisticAsync(() => Period.RemoveFundTransfer(id),
            acct => api.RemoveFundTransferAsync(acct, id), refetchAfter: false);

    // --- Cross-account transfers (money out -> a contribution in another account) ---

    /// <summary>Other accounts the money could be sent to: the user's other accounts in the same currency.</summary>
    public IReadOnlyList<AccountSummaryDto> TransferableAccounts =>
        _summaries.Where(a => a.Id != CurrentAccountId && a.Currency == Currency).ToList();

    public string AccountName(Guid accountId) =>
        _summaries.FirstOrDefault(a => a.Id == accountId)?.Name ?? "another account";

    /// <summary>Download the current account as an .xlsx (one sheet per period). Returns the file bytes + name.</summary>
    public Task<(byte[] Bytes, string FileName)> ExportCurrentAccountAsync() => api.ExportAccountAsync(CurrentAccountId);

    public ExternalTransfer? FindExternalTransfer(Guid id) =>
        Period.ExternalTransfers.FirstOrDefault(t => t.Id == id);

    /// <summary>The (root) funds of another account, for picking a transfer/settlement destination fund. Uses the
    /// warm cache when available, else fetches and deserializes the snapshot (read-only — not cached here).</summary>
    public async Task<IReadOnlyList<Fund>> LoadAccountFundsAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return [];
        if (_cache.TryGetValue(accountId, out var hit)) return hit.Account.RootFunds.ToList();
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload)) return [];
        return AccountSnapshotSerializer.Deserialize(snapshot.Payload).RootFunds.ToList();
    }

    /// <summary>
    /// Send money from one of this account's funds to another account: the server applies the outflow here and the
    /// matching deposit there in one atomic two-account save (same currency, capped at the source fund's balance).
    /// </summary>
    public async Task TransferToAccount(Guid destinationAccountId, Guid fromFundId, decimal amount, string? note, Guid destinationFundId = default)
    {
        if (amount <= 0m) return;
        await ExecuteAsync(id => api.TransferToAccountAsync(id, new TransferToAccountRequest(
            destinationAccountId, fromFundId, amount, destinationFundId, note, Today())));
        _cache.Remove(destinationAccountId); // its snapshot changed server-side — a switch must refetch
    }

    /// <summary>The (root) funds and categories of another account, for the settle-onto-account pickers.</summary>
    public async Task<(IReadOnlyList<Fund> Funds, IReadOnlyList<Category> Categories)> LoadAccountStructureAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return ([], []);
        var account = _cache.TryGetValue(accountId, out var hit)
            ? hit.Account
            : await DeserializeAccountAsync(accountId);
        return account is null ? ([], []) : (account.RootFunds.ToList(), account.Categories.ToList());
    }

    private async Task<Account?> DeserializeAccountAsync(Guid accountId)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        return string.IsNullOrEmpty(snapshot.Payload) ? null : AccountSnapshotSerializer.Deserialize(snapshot.Payload);
    }

    /// <summary>
    /// Settle (or re-settle) a portion of an "on behalf of another account" expense onto another account: the
    /// chosen amount becomes that account's own expense (in the picked fund + category) and the source expense is
    /// reduced by that amount, atomically in one two-account server save. The two are linked by a settlement id
    /// so edits/removals on either side keep the other in step. (Feature 1.)
    /// </summary>
    public async Task SettleExpenseToAccount(Guid sourceExpenseId, Guid destinationAccountId, Guid destinationFundId, Guid destinationCategoryId, decimal amount, string? note)
    {
        if (amount <= 0m) return;
        await ExecuteAsync(id => api.SettleExpenseAsync(id, sourceExpenseId, new SettleExpenseRequest(
            destinationAccountId, destinationFundId, destinationCategoryId, amount, note)));
        _cache.Remove(destinationAccountId);
    }

    /// <summary>Undo a settlement from the source side: remove the linked destination expense and restore the source's full amount.</summary>
    public async Task UnsettleExpense(Guid sourceExpenseId)
    {
        var source = Period.Expenses.FirstOrDefault(e => e.Id == sourceExpenseId);
        if (source is not { IsSettlementSource: true, SettledToAccountId: { } destAccount }) return;
        await ExecuteAsync(id => api.UnsettleExpenseAsync(id, sourceExpenseId, destAccount));
        _cache.Remove(destAccount);
    }

    /// <summary>Mirror a new settled amount onto the source expense in another account (0 un-settles it).</summary>
    // TODO(cutover): edit/remove of a settlement-LINKED expense still propagates client-side via the whole-snapshot
    // path below (the expense command endpoints don't keep the counterpart in step — a known server-side scope gap).
    private Task SyncSourceSettlementAmount(Guid sourceAccountId, Guid settlementId, decimal newAmount) =>
        MutateOtherAccountAsync(sourceAccountId, source =>
        {
            foreach (var p in source.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId && e.IsSettlementSource) is { } ex)
                {
                    p.SetSettlement(ex.Id, settlementId, CurrentAccountId, new Money(newAmount, source.Currency));
                    return;
                }
        });

    private Task RemoveLinkedSettlementExpense(Guid accountId, Guid settlementId) =>
        MutateOtherAccountAsync(accountId, account =>
        {
            foreach (var p in account.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId) is { } ex)
                {
                    p.RemoveExpense(ex.Id);
                    return;
                }
        });

    /// <summary>Load another account, apply a mutation, push it, and drop its cache entry so a switch refetches.</summary>
    private async Task MutateOtherAccountAsync(Guid accountId, Action<Account> mutate)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload))
            throw new InvalidOperationException("Open that account once before linking to it.");
        var account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        mutate(account);
        var payload = AccountSnapshotSerializer.Serialize(account);
        await api.SaveSnapshotAsync(accountId, new SaveAccountRequest(payload, snapshot.Version));
        _cache.Remove(accountId);
    }

    // One-sided removal: drops the outflow here and leaves the other account's deposit standing. Now only reachable
    // for transfers with no pair id — savings disbursements (which have no other account at all) and rows recorded
    // before the link existed. Everything else goes through RemoveAccountTransfer below.
    // TODO(cutover): no DELETE /transfers-out endpoint yet — still local-mutate + whole-snapshot push.
    public Task RemoveExternalTransfer(Guid id)
    {
        Period.RemoveExternalTransfer(id);
        return SaveAsync();
    }

    /// <summary>Whether both halves of this transfer can be found — i.e. it carries a pair id and names the other
    /// account. False for savings disbursements and for transfers recorded before the link existed.</summary>
    public bool IsLinkedAccountTransfer(ExternalTransfer transfer) =>
        transfer is { AccountTransferId: not null, ToAccountId: not null };

    /// <summary>Edit both halves of an account-to-account transfer. Two accounts change, so this uses the
    /// no-optimism spine: the other account isn't loaded here, and the server's result is the only truth.</summary>
    public async Task EditAccountTransfer(ExternalTransfer transfer, decimal amount, DateOnly date, Guid fromFundId, string? note)
    {
        if (amount <= 0m || transfer.AccountTransferId is not { } pairId || transfer.ToAccountId is not { } destination) return;
        await ExecuteAsync(id => api.EditAccountTransferAsync(id, pairId,
            new EditAccountTransferRequest(destination, amount, fromFundId, default, note, date)));
        _cache.Remove(destination);   // its deposit changed server-side — a switch must refetch
    }

    /// <summary>Remove both halves: the outflow here and the deposit it created in the other account.</summary>
    public async Task RemoveAccountTransfer(ExternalTransfer transfer)
    {
        if (transfer.AccountTransferId is not { } pairId || transfer.ToAccountId is not { } destination) return;
        await ExecuteAsync(id => api.RemoveAccountTransferAsync(id, pairId, destination));
        _cache.Remove(destination);
    }

    // --- Bank sync (Open Banking) -----------------------------------------
    // The server stages raw bank transactions; the client turns a confirmed one into a real domain expense
    // (the account body is client-owned) and then acks it so a later sync won't resurface it.

    public Task<BankSyncStatusDto> GetBankStatus() => api.GetBankStatusAsync(CurrentAccountId);

    public Task<List<BankInstitutionDto>> GetBankInstitutions(string country = "GB") =>
        api.GetBankInstitutionsAsync(CurrentAccountId, country);

    /// <summary>Begin linking: returns the bank's consent URL for the UI to navigate to.</summary>
    public async Task<string> StartBankLink(string institutionName, string country, string? logo = null)
    {
        var resp = await api.StartBankLinkAsync(CurrentAccountId, new StartBankLinkRequest(institutionName, country, logo));
        return resp.LinkUrl;
    }

    public Task SyncBank() => api.SyncBankAsync(CurrentAccountId);

    public Task<List<PendingBankTransactionDto>> GetPendingBankTransactions() =>
        api.GetPendingBankTransactionsAsync(CurrentAccountId);

    public Task<List<BankAccountDto>> GetBankAccounts() => api.GetBankAccountsAsync(CurrentAccountId);
    public Task SelectBankAccount(string bankAccountRef) => api.SelectBankAccountAsync(CurrentAccountId, bankAccountRef);

    /// <summary>Turn a staged bank transaction into an expense in the given category/fund, then mark it handled.</summary>
    // TODO(cutover): rides the local bank-provenance path until AddExpenseRequest carries bankExternalId/autoFiled.
    public async Task ConfirmBankTransaction(string externalId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, bool autoFiled = false)
    {
        await AddExpenseWithBankLink(categoryId, amount, fundId, note, date, externalId, autoFiled);
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    public Task DismissBankTransaction(string externalId) =>
        api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: false);

    // --- Bank de-duplication: pair incoming debits with existing un-linked entries (e.g. a manual log made while
    //     sync was down) so the user can replace one instead of double-counting. ---
    public record DuplicateSuggestion(Guid ExpenseId, Money Amount, Guid CategoryId, string Category, string CategoryIcon, string Fund, DateOnly Date);

    /// <summary>For the given pending bank debits, suggest an existing un-linked expense that looks like the same
    /// transaction (same amount, within a few days). Keyed by bank ExternalId. Read-only — suggests nothing binding.</summary>
    public IReadOnlyDictionary<string, DuplicateSuggestion> BankDuplicateSuggestions(IEnumerable<PendingBankTransactionDto> pendingDebits)
    {
        // Consider every real expense this period, not just un-linked manual ones: the same transaction can arrive
        // from two bank sources (statement import + live sync) with different ExternalIds, so an already-bank-linked
        // expense must still be offered as a "you already logged this" match. Disbursements (savings payouts) excluded.
        var entries = Period.Expenses
            .Where(e => e.SourceSavingCategoryId is null)
            .Select(e => new BankDuplicateMatcher.Entry(e.Id, e.Amount.Amount, e.Date));
        var debits = pendingDebits.Where(t => t.Amount < 0m)
            .Select(t => new BankDuplicateMatcher.Pending(t.ExternalId, t.Amount, t.Date));

        var map = new Dictionary<string, DuplicateSuggestion>();
        foreach (var s in BankDuplicateMatcher.Suggest(debits, entries, windowDays: 4))
        {
            var e = Period.Expenses.First(x => x.Id == s.ExpenseId);
            map[s.ExternalId] = new DuplicateSuggestion(e.Id, e.Amount, e.CategoryId,
                CategoryName(e.CategoryId), CategoryIcon(e.CategoryId), FundName(e.FundId), e.Date);
        }
        return map;
    }

    /// <summary>True when this period already holds an expense that looks like the same transaction as an incoming
    /// bank debit (same absolute amount, within a few days). Used to hold a mapped <b>auto-file</b> back into manual
    /// review instead of silently double-posting over a recurring-posted or already-imported entry — auto-file is the
    /// one path that otherwise never sees the duplicate matcher. Disbursements (savings payouts) are excluded.</summary>
    public bool HasLikelyDuplicateExpense(decimal amount, DateOnly date, int windowDays = 4)
    {
        var abs = Math.Abs(amount);
        return Period.Expenses.Any(e => e.SourceSavingCategoryId is null && e.Amount.Amount == abs
            && Math.Abs(e.Date.DayNumber - date.DayNumber) <= windowDays);
    }

    /// <summary>The incoming bank debit is the same as a manual entry: drop the manual (often mis-filed) expense and
    /// confirm the bank row into that entry's category on the synced fund — one clean, bank-linked expense, no double.</summary>
    public async Task ReplaceWithBankTransaction(string externalId, Guid manualExpenseId, decimal amount, DateOnly date, string? note)
    {
        var exp = Period.Expenses.FirstOrDefault(e => e.Id == manualExpenseId);
        var categoryId = exp?.CategoryId ?? AllCategories.FirstOrDefault()?.Id ?? Guid.Empty;
        var fund = HasSyncedFund ? SyncedFundId : (exp?.FundId ?? Guid.Empty);
        if (exp is not null) Period.RemoveExpense(manualExpenseId);
        // ConfirmBankTransaction's AddExpense does the single SaveAsync (covering the removal too) then acks the row.
        await ConfirmBankTransaction(externalId, categoryId, amount, fund, note, date);
    }

    /// <summary>Turn a bank money-in into a movement into the synced fund: the destination is the synced fund
    /// (not credited — the real balance handles it); the <paramref name="source"/> ("fund:{id}" or
    /// "contributor:{id}") is where it came from and is the side that actually moves. Then acks the row.</summary>
    public async Task ConfirmBankMoneyIn(string externalId, string source, decimal amount, string? note, DateOnly date, bool autoFiled = false)
    {
        if (!HasSyncedFund) throw new InvalidOperationException("Mark a fund as synced to your bank first (Edit fund).");
        var parts = (source ?? "").Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var targetId))
            throw new InvalidOperationException("Pick where this money came from.");

        if (parts[0] == "fund")
        {
            if (targetId == SyncedFundId) throw new InvalidOperationException("The source can't be the synced fund itself.");
            var transfer = Period.TransferFunds(targetId, SyncedFundId, Money(amount), date, note);
            transfer.SetSyncedSides(FundIsSynced(targetId), toSynced: true);   // synced destination isn't credited
            transfer.SetBankLink(externalId, autoFiled);                       // bank provenance + auto-filed badge
        }
        else if (parts[0] == "contributor")
        {
            var deposit = Period.Deposit(targetId, Money(amount), fundId: SyncedFundId, date: date);
            deposit.SetFundSynced(true);   // counts as a contribution, but the synced fund isn't credited
        }
        else throw new InvalidOperationException("Unknown money-in source.");

        await SaveAsync();
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    /// <summary>Route a bank money-out (debit) as a <b>transfer</b> instead of an expense: from the synced fund to
    /// another fund in this account (<c>"fund:{id}"</c>) or to another account (<c>"acct:{accountId}:{fundId}"</c>).
    /// The synced source isn't debited (the real bank balance handles it); the destination side actually moves. Then
    /// acks the row so it drops from review.</summary>
    public async Task ConfirmBankMoneyOutAsTransfer(string externalId, string destination, decimal amount, string? note, DateOnly date)
    {
        if (!HasSyncedFund) throw new InvalidOperationException("Mark a fund as synced to your bank first (Edit fund).");
        if (amount <= 0m) throw new InvalidOperationException("Amount must be positive.");
        var parts = (destination ?? "").Split(':');

        if (parts.Length == 2 && parts[0] == "fund" && Guid.TryParse(parts[1], out var destFund))
        {
            if (destFund == SyncedFundId) throw new InvalidOperationException("The destination can't be the synced fund itself.");
            var transfer = Period.TransferFunds(SyncedFundId, destFund, Money(amount), date, note);
            transfer.SetSyncedSides(true, FundIsSynced(destFund));   // synced source isn't debited — the bank balance handles it
            transfer.SetBankLink(externalId, autoFiled: false);
            await SaveAsync();
            await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
        }
        else if (parts.Length == 3 && parts[0] == "acct"
                 && Guid.TryParse(parts[1], out var destAcct) && Guid.TryParse(parts[2], out var destAcctFund))
        {
            await TransferToAccount(destAcct, SyncedFundId, amount, note, destAcctFund);   // outflow here, deposit there
            await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
        }
        else throw new InvalidOperationException("Pick where this money went.");
    }

    // --- Consent (audit-logged) -------------------------------------------
    public Task RecordConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: true);
    public Task WithdrawConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: false);

    /// <summary>Drop the current account's bank connection so it can be linked again. Withdraws link + sync consent.</summary>
    public async Task DisconnectBank()
    {
        var id = CurrentAccountId;
        await api.DisconnectBankAsync(id);
        try { await api.RecordConsentAsync("bank_sync", id, granted: false); await api.RecordConsentAsync("bank_link", id, granted: false); }
        catch { /* audit best-effort */ }
    }

    /// <summary>Re-open handled bank rows in a date range (e.g. after a period is deleted) so they resurface.</summary>
    public Task ResetBankRange(DateOnly from, DateOnly to) => api.ResetBankRangeAsync(CurrentAccountId, from, to);

    public Task<List<BankMappingDto>> GetBankMappings() => api.GetBankMappingsAsync(CurrentAccountId);
    public Task SetBankMapping(string description, string kind, Guid targetId) =>
        api.SetBankMappingAsync(CurrentAccountId, description, kind, targetId);
    public Task RemoveBankMapping(string description) => api.RemoveBankMappingAsync(CurrentAccountId, description);

    /// <summary>Normalize a bank description to the same key the server matches rules against (MatchKeyOf).</summary>
    public static string BankMatchKey(string description) =>
        string.Join(' ', (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Split a description (or a rule's match-key) into lowercased word tokens, on any non-letter/non-digit
    /// boundary — so "TESCO,LONDON 4471" → [tesco, london, 4471] and Cyrillic merchant names survive. A saved rule
    /// matches a transaction when <b>all</b> of the rule's tokens are present in the transaction's tokens (the user
    /// picks which tokens carry the merchant's identity), and the most-specific matching rule wins.</summary>
    public static IReadOnlyList<string> BankTokens(string s) =>
        System.Text.RegularExpressions.Regex
            .Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length > 0).ToList();

    // Words that carry no merchant identity — legal suffixes, payment noise, common stopwords — dropped when
    // reducing a description to its stem so store numbers and legal forms don't split one merchant into many rules.
    private static readonly HashSet<string> BankStemStop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "ltd", "ltda", "llc", "inc", "gmbh", "plc", "corp", "llp", "group",
        "ad", "ead", "ood", "eood", "jsc", "sa", "bv", "oy", "ab", "co", "com",
        "card", "payment", "pos", "purchase", "pmt", "trans", "www",
    };

    /// <summary>An aggressive merchant "stem": the first significant word of a description (letters only, 3+ chars, not
    /// a legal suffix / payment stopword), lowercased. Lets variants like "Fantastico 30" and "Fantastico Group Ltd"
    /// share one auto-map rule. Empty when there's no significant word (then only exact matching applies).</summary>
    public static string BankMatchStem(string description)
    {
        foreach (var raw in (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = new string(raw.Where(char.IsLetter).ToArray());
            if (tok.Length >= 3 && !BankStemStop.Contains(tok)) return tok;
        }
        return "";
    }

    // The server identifies the period positionally (oldest = 0) — the same index this state navigates by.
    public Task ReschedulePeriod(DateOnly from, DateOnly to)
    {
        var period = Period;
        return ExecuteOptimisticAsync(() => Account.ReschedulePeriod(period, from, to),
            id => api.ReschedulePeriodAsync(id, _selectedIndex, new ReschedulePeriodRequest(from, to)), refetchAfter: true);
    }

    // Category CRUD
    public async Task<Guid> AddCategory(string name, Guid? parentId, string? icon = null, bool essential = false)
    {
        var result = await ExecuteOptimisticAsync(() =>
        {
            var category = Account.AddCategory(name, parentId, icon);
            if (essential) Account.SetCategoryEssential(category.Id, true);
        },
        id => api.CreateCategoryAsync(id, new CreateCategoryRequest(name, parentId, icon, essential)),
        refetchAfter: true);
        return result.EntityId ?? Guid.Empty;
    }

    /// <summary>Whether a category is flagged essential (rent/groceries/health...). Advisory only.</summary>
    public bool CategoryIsEssential(Guid categoryId) => Account.FindCategory(categoryId)?.IsEssential ?? false;

    /// <summary>Each <b>discretionary</b> (non-essential) category that carries its own budget and has spare this period
    /// (allocated − spent, where positive), biggest first. Advisory input for the "put spare toward a debt" nudge;
    /// moves nothing.</summary>
    public IReadOnlyList<(Guid Id, string Name, decimal Amount)> DiscretionaryLeftovers()
    {
        var list = new List<(Guid Id, string Name, decimal Amount)>();
        foreach (var c in AllCategories)
        {
            if (c.IsEssential || !HasBudget(c.Id)) continue;
            var cov = Coverage(c.Id);
            var left = cov.Allocated.Amount - cov.Spent.Amount;
            if (left > 0m) list.Add((c.Id, c.Name, left));
        }
        return list.OrderByDescending(x => x.Amount).ToList();
    }

    /// <summary>Best-guess "is this essential?" from the name, to pre-tick the flag on new categories (user can change it).</summary>
    private static readonly string[] EssentialWords =
        { "rent", "mortgage", "housing", "grocer", "food", "health", "medic", "pharmac", "doctor", "dental",
          "utilit", "electric", "water", "gas", "heating", "insurance", "transport", "fuel", "petrol", "commute",
          "childcare", "school", "tuition", "nursery", "loan", "debt", "council tax", "bills" };
    public static bool GuessEssential(string? name) =>
        !string.IsNullOrWhiteSpace(name) && EssentialWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    // The category edit endpoint takes (name, icon, essential?) together, so single-field setters pass the
    // current values of the fields they don't change (a null Essential leaves the flag untouched server-side).
    public Task RenameCategory(Guid categoryId, string name)
    {
        var icon = CategoryStoredIcon(categoryId);
        return ExecuteOptimisticAsync(() => Account.RenameCategory(categoryId, name),
            id => api.EditCategoryAsync(id, categoryId, new EditCategoryRequest(name, icon)), refetchAfter: true);
    }

    /// <summary>Rename a category and set its icon in one save.</summary>
    public Task EditCategory(Guid categoryId, string name, string? icon) =>
        ExecuteOptimisticAsync(() =>
        {
            Account.RenameCategory(categoryId, name);
            Account.SetCategoryIcon(categoryId, icon);
        },
        id => api.EditCategoryAsync(id, categoryId, new EditCategoryRequest(name, icon)), refetchAfter: true);

    /// <summary>Set a category's essential/discretionary flag (advisory only).</summary>
    public Task SetCategoryEssential(Guid categoryId, bool essential)
    {
        var (name, icon) = (Account.FindCategory(categoryId)?.Name ?? "", CategoryStoredIcon(categoryId));
        return ExecuteOptimisticAsync(() => Account.SetCategoryEssential(categoryId, essential),
            id => api.EditCategoryAsync(id, categoryId, new EditCategoryRequest(name, icon, essential)), refetchAfter: true);
    }

    /// <summary>The icon to show for a category — its explicit choice, or one guessed from the name.</summary>
    public string CategoryIcon(Guid categoryId)
    {
        var c = Account.FindCategory(categoryId);
        return CategoryIcons.Effective(c?.Icon, c?.Name);
    }

    /// <summary>The category's explicitly-stored icon (null when none) — for pre-selecting the edit picker.</summary>
    public string? CategoryStoredIcon(Guid categoryId) => Account.FindCategory(categoryId)?.Icon;

    public bool CategoryIsArchived(Guid categoryId) => Account.FindCategory(categoryId)?.IsArchived ?? false;

    public Task RemoveCategory(Guid categoryId) =>
        ExecuteOptimisticAsync(() => Account.RemoveCategory(categoryId),
            id => api.RemoveCategoryAsync(id, categoryId), refetchAfter: false);

    /// <summary>Delete a category that history references, moving its expenses (and its sub-categories') to
    /// <paramref name="moveToCategoryId"/>. Re-fetches: the sweep touches rows across every period, so the local
    /// aggregate should be replaced by the server's rather than trusted to have matched it move for move.</summary>
    public Task RemoveCategoryMovingExpenses(Guid categoryId, Guid moveToCategoryId) =>
        ExecuteOptimisticAsync(() => Account.RemoveCategoryReassigning(categoryId, moveToCategoryId),
            id => api.RemoveCategoryAsync(id, categoryId, moveToCategoryId), refetchAfter: true);

    /// <summary>How many expenses across all periods a delete would have to re-file (the category + its subs).</summary>
    public int ExpensesUnderCategory(Guid categoryId)
    {
        var ids = Account.Categories.Where(c => c.Id == categoryId || c.ParentId == categoryId).Select(c => c.Id).ToHashSet();
        return Account.Periods.SelectMany(p => p.Expenses).Count(e => ids.Contains(e.CategoryId));
    }

    /// <summary>Where a deleted category's history could go: every other category that will still exist afterwards
    /// (so, not its own sub-categories). Archived ones are excluded — history shouldn't land somewhere hidden.</summary>
    public IReadOnlyList<(Category Category, int Depth)> ReassignTargetsFor(Guid categoryId) =>
        CategoryOptions.Where(o => o.Category.Id != categoryId
                                && o.Category.ParentId != categoryId
                                && !o.Category.IsArchived).ToList();

    /// <summary>Archive a category (hide it, keep its expenses/budgets in history). No reference blocker — unlike
    /// <see cref="RemoveCategory"/> this works even when expenses/budgets/sub-categories reference it.</summary>
    public Task ArchiveCategory(Guid categoryId) =>
        ExecuteOptimisticAsync(() => Account.SetCategoryArchived(categoryId, true),
            id => api.SetCategoryArchivedAsync(id, categoryId, true), refetchAfter: true);
    public Task RestoreCategory(Guid categoryId) =>
        ExecuteOptimisticAsync(() => Account.SetCategoryArchived(categoryId, false),
            id => api.SetCategoryArchivedAsync(id, categoryId, false), refetchAfter: true);

    // --- Tags: flat, cross-cutting labels attached to expenses (sit alongside sub-categories) ---
    /// <summary>Active (non-archived) tags — the pickers. Ordered by name for a stable list.</summary>
    public IReadOnlyList<Tag> ActiveTags => Account.ActiveTags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    /// <summary>All tags including archived — for the manage-tags surface.</summary>
    public IReadOnlyList<Tag> AllTags => Account.Tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    public Tag? FindTag(Guid tagId) => Account.FindTag(tagId);
    public string TagName(Guid tagId) => Account.FindTag(tagId)?.Name ?? "—";
    public string? TagIcon(Guid tagId) => Account.FindTag(tagId)?.Icon;
    public bool TagIsArchived(Guid tagId) => Account.FindTag(tagId)?.IsArchived ?? false;

    /// <summary>The active tags attached to an expense, resolved to entities (dangling ids from removed tags are dropped).</summary>
    public IReadOnlyList<Tag> ExpenseTags(FinApp.Domain.Budgeting.Expense expense) =>
        expense.TagIds.Select(id => Account.FindTag(id)).Where(t => t is not null).Select(t => t!).ToList();

    public async Task<Guid> AddTag(string name, string? icon = null)
    {
        var result = await ExecuteOptimisticAsync(() => { Account.AddTag(name, icon); },
            id => api.CreateTagAsync(id, new CreateTagRequest(name, icon)), refetchAfter: true);
        return result.EntityId ?? Guid.Empty;
    }
    /// <summary>The id of the tag called <paramref name="name"/>, creating it if there isn't one (case-insensitive, and
    /// an archived match is restored rather than duplicated). Lets a flow that needs a specific tag — the installment
    /// split's "Loan principal"/"Loan interest" — get one without making the user set it up first.</summary>
    public async Task<Guid> EnsureTag(string name)
    {
        var existing = Account.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null) return await AddTag(name);
        if (existing.IsArchived) await RestoreTag(existing.Id);
        return existing.Id;
    }

    /// <summary>The category this tag files new expenses into (F2), or null when it carries no filing opinion.
    /// Resolves to null for a category that no longer exists, so a stale binding reads as "unbound" rather than
    /// pre-selecting something the pickers can't show.</summary>
    public Guid? TagCategory(Guid tagId) =>
        Account.FindTag(tagId)?.CategoryId is { } cid && Account.FindCategory(cid) is not null ? cid : null;

    public Task SaveTag(Guid tagId, string name, string? icon, Guid? categoryId = null) =>
        ExecuteOptimisticAsync(() => { Account.RenameTag(tagId, name); Account.SetTagIcon(tagId, icon); Account.SetTagCategory(tagId, categoryId); },
            id => api.EditTagAsync(id, tagId, new EditTagRequest(name, icon, categoryId)), refetchAfter: true);
    public Task ArchiveTag(Guid tagId) =>
        ExecuteOptimisticAsync(() => Account.SetTagArchived(tagId, true),
            id => api.SetTagArchivedAsync(id, tagId, true), refetchAfter: true);
    public Task RestoreTag(Guid tagId) =>
        ExecuteOptimisticAsync(() => Account.SetTagArchived(tagId, false),
            id => api.SetTagArchivedAsync(id, tagId, false), refetchAfter: true);
    public Task RemoveTag(Guid tagId) =>
        ExecuteOptimisticAsync(() => Account.RemoveTag(tagId),
            id => api.RemoveTagAsync(id, tagId), refetchAfter: true);

    // --- Trips: a named journey expenses point at ------------------------------------------------------------
    /// <summary>Today, as the rest of the app reckons it — so a view can ask a trip whether it's running without
    /// minting its own clock (and disagreeing with <see cref="ActiveTrip"/> by a day around midnight).</summary>
    public DateOnly TodayDate => Today();

    /// <summary>Every trip, newest departure first — the trips list.</summary>
    public IReadOnlyList<Trip> AllTrips => Account.TripsByDeparture.ToList();
    public Trip? FindTrip(Guid tripId) => Account.FindTrip(tripId);
    public string TripName(Guid tripId) => Account.FindTrip(tripId)?.Name ?? "—";
    public string? TripIcon(Guid tripId) => Account.FindTrip(tripId)?.Icon;

    /// <summary>The trip "today" falls inside, or null when we're not travelling — this is what trip mode means.
    /// Derived from the dates on every read, so there is no state that can be left switched on.</summary>
    public Trip? ActiveTrip => Account.ActiveTrip(Today());

    /// <summary>True while a trip is running: the expense form defaults to it, Home wears the trip banner, and the
    /// over-budget strip changes tone rather than nagging through someone's holiday.</summary>
    public bool InTripMode => ActiveTrip is not null;

    /// <summary>Trips that haven't started yet, soonest first — what the "part of a trip" picker offers alongside
    /// the running ones, so a booking paid today can be filed against next summer.</summary>
    public IReadOnlyList<Trip> UpcomingTrips => Account.UpcomingTrips(Today()).ToList();

    /// <summary>
    /// <b>Every</b> trip today falls inside, latest departure first — not just the one <see cref="ActiveTrip"/>
    /// resolves to.
    /// <para>
    /// ★ The distinction only shows up when trips overlap, and there it matters: <c>ActiveTrip</c> picks a single
    /// winner so that "what does the app wear, and what does the form default to" has one answer. Offering only
    /// that winner in the picker made the *other* trip you are genuinely on unselectable — fly into a city for a
    /// conference during a longer holiday and the holiday disappears from the form. Defaulting to one is a
    /// convenience; hiding the other is the app deciding which trip you are on.
    /// </para>
    /// </summary>
    public IReadOnlyList<Trip> RunningTrips =>
        Account.Trips.Where(t => t.IsActiveOn(Today())).OrderByDescending(t => t.From).ToList();

    /// <summary>The trip labels (Stay, Travel, Food &amp; drink…), the axis a trip's cost split is drawn on. Empty
    /// until the first trip seeds them.</summary>
    public IReadOnlyList<Tag> TripTags => Account.TripTags.Where(t => !t.IsArchived).ToList();

    /// <summary>Tags for the everyday picker — the trip labels are left out, since "Tickets &amp; tours" is noise
    /// when you're logging groceries. Inside trip mode the caller shows <see cref="TripTags"/> instead.</summary>
    public IReadOnlyList<Tag> EverydayTags => ActiveTags.Where(t => !t.IsTripTag).ToList();

    public async Task<Guid> AddTrip(string name, DateOnly from, DateOnly to, string? destination = null, string? icon = null)
    {
        var result = await ExecuteOptimisticAsync(() => { Account.AddTrip(name, from, to, destination, icon); },
            id => api.CreateTripAsync(id, new CreateTripRequest(name, from, to, destination, icon)), refetchAfter: true);
        return result.EntityId ?? Guid.Empty;
    }

    /// <summary>Save a trip's whole intended state — the same full-replace shape as the endpoint, so an omitted
    /// field means "no longer set". Moving the dates never detaches expenses.</summary>
    public Task SaveTrip(Guid tripId, string name, DateOnly from, DateOnly to, string? destination = null, string? icon = null,
        Guid? savingCategoryId = null, decimal? budget = null, string? spendCurrency = null, decimal? rate = null,
        Guid? categoryId = null) =>
        ExecuteOptimisticAsync(() =>
        {
            Account.UpdateTrip(tripId, name, from, to, destination, icon);
            Account.SetTripSavingCategory(tripId, savingCategoryId);
            Account.SetTripCategory(tripId, categoryId);
            Account.SetTripBudget(tripId, budget);
            Account.SetTripRate(tripId, spendCurrency, rate);
        },
            id => api.EditTripAsync(id, tripId, new EditTripRequest(name, from, to, destination, icon, savingCategoryId, budget, spendCurrency, rate, categoryId)),
            refetchAfter: true);

    /// <summary>Trips whose dates have arrived but that nobody has confirmed leaving on. Trip mode is opt-in on the
    /// day — a date is not a departure.</summary>
    public IReadOnlyList<Trip> TripsAwaitingStart => Account.TripsAwaitingStart(Today()).ToList();

    /// <summary>Confirm a trip has begun (or take it back). This is the tap that turns trip mode on.</summary>
    public Task StartTrip(Guid tripId, bool started = true) =>
        ExecuteOptimisticAsync(() =>
        {
            if (started) Account.StartTrip(tripId, Today());
            else Account.UnstartTrip(tripId);
        },
            id => api.StartTripAsync(id, tripId, started), refetchAfter: true);

    /// <summary>Declare a trip over as of today, or put it back on the road. "Over" is a stored fact, not a date
    /// comparison — see <c>Trip.FinishedOn</c> for why pulling the end date in wasn't enough.</summary>
    public Task FinishTrip(Guid tripId, bool finished = true) =>
        ExecuteOptimisticAsync(() =>
        {
            if (finished) Account.FinishTrip(tripId, Today());
            else Account.ReopenTrip(tripId);
        },
            id => api.FinishTripAsync(id, tripId, finished), refetchAfter: true);

    /// <summary>Release money saved for a trip into that trip's budget for this period. Both halves — the period's
    /// saving→budget maturing and the trip's own record of it — happen in one server mutation.</summary>
    public Task UseTripSavings(Guid tripId, decimal amount, string? note) =>
        ExecuteOptimisticAsync(() =>
        {
            var trip = Account.FindTrip(tripId) ?? throw new InvalidOperationException("Trip not found.");
            Account.ApplyTripSavings(tripId, amount);
            Period.ConvertSavingToBudget(trip.SavingCategoryId!.Value, trip.CategoryId!.Value, Money(amount), Today(), note);
        },
            id => api.UseTripSavingsAsync(id, tripId, new UseTripSavingsRequest(amount, Today(), note)), refetchAfter: true);

    public Task RemoveTrip(Guid tripId) =>
        ExecuteOptimisticAsync(() => Account.RemoveTrip(tripId),
            id => api.RemoveTripAsync(id, tripId), refetchAfter: true);

    /// <summary>Attach an expense to a trip, or detach it with null. Works on any period's expense — attaching last
    /// March's flight to this June's trip is the point.</summary>
    public Task SetExpenseTrip(Guid expenseId, Guid? tripId) =>
        ExecuteOptimisticAsync(() =>
        {
            var expense = Account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId);
            expense?.SetTrip(tripId);
        },
            id => api.SetExpenseTripAsync(id, expenseId, tripId), refetchAfter: true);

    /// <summary>Label one expense (or clear it) in any period — the trip labels are applied to bookings that sit in
    /// months closed long before the trip is reviewed. Writes only the tag; nothing about the money changes.</summary>
    public Task SetExpenseTag(Guid expenseId, Guid? tagId) =>
        ExecuteOptimisticAsync(() =>
        {
            var expense = Account.Periods.SelectMany(p => p.Expenses).FirstOrDefault(e => e.Id == expenseId);
            expense?.SetTag(tagId);
        },
            id => api.SetExpenseTagAsync(id, expenseId, tagId), refetchAfter: true);

    /// <summary>Create the trip labels if they don't exist. The caller passes localized names — the server seeds
    /// once and ignores later calls, so switching language can't fork the split into two tag sets.</summary>
    public Task EnsureTripTags(IReadOnlyList<TripTagSeed> seeds) =>
        ExecuteOptimisticAsync(() => Account.EnsureTripTags(seeds.Select(s => (s.Name, s.Icon, s.CategoryId))),
            id => api.SeedTripTagsAsync(id, new SeedTripTagsRequest(seeds)), refetchAfter: true);

    /// <summary>What a trip cost, split by where it went — see <c>TripRecapService</c>.</summary>
    public TripRecap? TripRecap(Guid tripId) => new TripRecapService().Build(Account, tripId);

    /// <summary>Every trip's recap, newest departure first — the trips list.</summary>
    public IReadOnlyList<TripRecap> TripRecaps() => new TripRecapService().BuildAll(Account);

    /// <summary>The expenses attached to a trip, newest first.</summary>
    public IReadOnlyList<FinApp.Domain.Budgeting.Expense> TripExpenses(Guid tripId) =>
        Account.TripExpenses(tripId).ToList();

    /// <summary>
    /// Expenses across <b>every</b> period, newest first — the pool the "attach something you've already paid"
    /// picker draws from.
    /// <para>
    /// Spanning periods is the requirement, not a convenience: the flight that belongs to a June trip was paid in
    /// March, and by the time the trip exists that period is closed. Nothing here edits the expense — attaching
    /// only writes the link — so a closed period is no obstacle.
    /// </para>
    /// </summary>
    /// <param name="search">Free text matched against the note and the category name. Null/blank returns the most
    /// recent <paramref name="take"/>; a search runs over <b>every</b> period first and only then takes the cap, so
    /// a flight bought last winter is reachable by typing "flight" rather than by scrolling to it.</param>
    /// <param name="alwaysInclude">A trip whose rows must never fall off the end — the ones already attached to the
    /// trip being edited. Without this, ticking a row could make it vanish from the list you ticked it in.</param>
    public IReadOnlyList<FinApp.Domain.Budgeting.Expense> RecentExpensesAcrossPeriods(
        int take = 60, string? search = null, Guid? alwaysInclude = null)
    {
        var all = Account.Periods.SelectMany(p => p.Expenses)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
            all = all.Where(e =>
                (e.Note ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || CategoryName(e.CategoryId).Contains(term, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id);

        var page = all.Take(take).ToList();
        if (alwaysInclude is { } tripId)
        {
            var pinned = Account.Periods.SelectMany(p => p.Expenses)
                .Where(e => e.TripId == tripId && page.All(x => x.Id != e.Id));
            page = page.Concat(pinned).OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).ToList();
        }
        return page;
    }

    /// <summary>How many expenses exist in total — so the attach list can say what it is NOT showing rather than
    /// letting a cap look like the end of the history.</summary>
    public int AllExpensesCount => Account.Periods.Sum(p => p.Expenses.Count);

    // Budget CRUD (the endpoint takes the threshold as a percent 0–100, same as these signatures)
    public Task SaveBudget(Guid categoryId, decimal amount, decimal thresholdPercent, bool notifyEvery) =>
        ExecuteOptimisticAsync(() => Period.SetBudget(categoryId, Money(amount), thresholdPercent / 100m, notifyEvery),
            id => api.SetBudgetAsync(id, categoryId, new SetBudgetRequest(amount, thresholdPercent, notifyEvery)), refetchAfter: true);

    /// <summary>Reallocate spare budget toward a debt in one step: trim <paramref name="categoryId"/>'s budget to
    /// <paramref name="newBudget"/> and set <paramref name="amount"/> aside toward <paramref name="savingCategoryId"/>.
    /// Backs the "Move it to the loan" nudge action — one save, so the spare disappears and the earmark grows together.</summary>
    public Task ReallocateBudgetToSaving(Guid categoryId, decimal newBudget, decimal thresholdPercent, bool notifyEvery,
        Guid savingCategoryId, decimal amount) =>
        ExecuteOptimisticAsync(() =>
        {
            Period.SetBudget(categoryId, Money(newBudget), thresholdPercent / 100m, notifyEvery);
            Period.AllocateToSavings(savingCategoryId, Money(amount), Today(), null, PriorSaved);
        },
        id => api.ReallocateToSavingsAsync(id, new ReallocateToSavingsRequest(
            categoryId, newBudget, thresholdPercent, notifyEvery, savingCategoryId, amount, Today())),
        refetchAfter: true);

    public Task RemoveBudget(Guid categoryId) =>
        ExecuteOptimisticAsync(() => Period.RemoveBudget(categoryId),
            id => api.RemoveBudgetAsync(id, categoryId), refetchAfter: false);

    /// <summary>Remove the latest period and make the previous one active again.</summary>
    public async Task RemoveLatestPeriod()
    {
        // Fix _selectedIndex INSIDE the optimistic apply: ExecuteOptimisticAsync repaints immediately after the
        // mutation, and if we were viewing the (now-removed) latest period the stale index would point past the end
        // and Period => Periods[_selectedIndex] would throw during that repaint. Clamp before the render, not after.
        await ExecuteOptimisticAsync(() => { Account.RemoveLatestPeriod(); _selectedIndex = Account.Periods.Count - 1; },
            id => api.RemoveLatestPeriodAsync(id), refetchAfter: false);
        RaiseChanged();
    }

    /// <summary>
    /// Start the next period (server-side: close the current one, open the next calendar month). The caller passes
    /// each top-level fund's real current balance, which becomes the new period's opening balance; a synced fund's
    /// live bank balance travels as <paramref name="syncedFundClosingBalance"/> (the server can't read the bank) and
    /// is stored as an informative-only opening.
    /// </summary>
    private bool _startingNext;

    public async Task StartNextPeriod(bool copyBudgets, IReadOnlyDictionary<Guid, decimal> realFundOpenings,
        bool adjustBudgets = false, decimal? syncedFundClosingBalance = null)
    {
        // Double-submit guard. The old local path closed the period synchronously, so a second click bailed on
        // CanStartNextPeriod; now the state only advances when the server's result lands, so hold re-entry too.
        if (!CanStartNextPeriod || _startingNext) return;
        _startingNext = true;
        try
        {
            await ExecuteAsync(id => api.StartNextPeriodAsync(id, new StartNextPeriodRequest(
                copyBudgets, adjustBudgets, realFundOpenings, syncedFundClosingBalance, Today())));
            _selectedIndex = Account.Periods.Count - 1;
            RaiseChanged();
        }
        finally { _startingNext = false; }
    }

    /// <summary>For the currently-viewed CLOSED period, the synced fund's balance captured at that period's rollover
    /// (= the following period's recorded informative opening). Null on the open period, or when no snapshot exists
    /// (e.g. a period closed before this was captured) — the caller then falls back to the live balance.</summary>
    public Money? SyncedFundClosingBalance(Guid fundId)
    {
        if (_selectedIndex >= Account.Periods.Count - 1) return null;   // open/latest period → use the live balance
        var successorOpening = Account.Periods[_selectedIndex + 1].InitialBalances.FirstOrDefault(b => b.FundId == fundId);
        return successorOpening is null || successorOpening.Amount.IsZero ? null : successorOpening.Amount;
    }

    // --- Invitations ------------------------------------------------------

    public IReadOnlyList<InvitationDto> PendingInvitations => _pendingInvitations;
    public int PendingInvitationCount => _pendingInvitations.Count;

    public async Task RefreshInvitationsAsync()
    {
        _pendingInvitations = await api.GetPendingInvitationsAsync();
        RaiseChanged();
    }

    public Task InviteToCurrentAccount(string username) => api.InviteAsync(CurrentAccountId, username);

    public async Task AcceptInvitation(Guid invitationId)
    {
        var accountId = await api.AcceptInvitationAsync(invitationId);
        await sync.SubscribeAsync(accountId);
        _summaries = await api.GetAccountsAsync();
        _accountIndex = Math.Max(0, _summaries.FindIndex(a => a.Id == accountId));
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();
        RaiseChanged();
    }

    public async Task DeclineInvitation(Guid invitationId)
    {
        await api.DeclineInvitationAsync(invitationId);
        await RefreshInvitationsAsync();
    }

    // --- Live sync handlers (fire on a background thread) ------------------

    private async void OnAccountChanged(AccountChangedEvent e)
    {
        if (e.ChangedByUserId == auth.UserId) return; // our own change is already applied locally + cached

        _cache.Remove(e.AccountId); // a contributor changed it — drop the stale entry (re-fetched on next view)

        if (_account is null || e.AccountId != _account.Id) return; // not the account in view: lazy refresh later
        try
        {
            var snapshot = await api.GetSnapshotAsync(e.AccountId);
            if (!string.IsNullOrEmpty(snapshot.Payload))
            {
                _version = snapshot.Version;
                _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
                ReconcileHeader(_account, _summaries[_accountIndex]);
                _cache[e.AccountId] = new CachedAccount(_account, _version);
                _selectedIndex = Math.Min(_selectedIndex, _account.Periods.Count - 1);
                RaiseChanged();
            }
        }
        catch { /* a transient reload failure shouldn't crash the UI */ }
    }

    /// <summary>On reconnect the hub's group memberships are gone and changes during the outage were missed, so
    /// drop the whole cache, re-join every account's channel, and refresh the one in view from the server.</summary>
    private async void OnReconnected()
    {
        try
        {
            _cache.Clear();
            await SubscribeAllAsync();
            if (_account is not null) { await LoadSelectedAccountAsync(forceRefresh: true); RaiseChanged(); }
        }
        catch { /* best effort */ }
    }

    /// <summary>Join the live channel for every account the user belongs to, so AccountChanged fires for all of
    /// them (and can invalidate their cache entries) — not just the one currently open.</summary>
    private async Task SubscribeAllAsync()
    {
        foreach (var s in _summaries)
        {
            try { await sync.SubscribeAsync(s.Id); } catch { /* best effort */ }
        }
    }

    private async void OnInvitationReceived(InvitationReceivedEvent e)
    {
        try { await RefreshInvitationsAsync(); } catch { /* best effort */ }
    }

    // --- Helpers ----------------------------------------------------------

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    private static bool NameEquals(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The legacy whole-snapshot save — now only for the flows without a command endpoint yet (bank
    /// confirms, achievements stamping, account settings, fund synced flag; each marked TODO(cutover)). Everything
    /// else goes through <see cref="ExecuteAsync"/>. Safe to mix: the PUT is version-checked (409 on conflict).</summary>
    private Task SaveAsync()
    {
        RaiseChanged();
        return PushSnapshotAsync();
    }
}
