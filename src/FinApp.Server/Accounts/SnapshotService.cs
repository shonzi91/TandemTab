using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Services;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Accounts;

/// <summary>
/// Stores and serves the full-account snapshot for shared accounts. Any contributor may read or write it; writes
/// use optimistic concurrency on <see cref="AccountSnapshotRow.Version"/> so concurrent editors can't silently
/// clobber each other. This service doesn't interpret the payload (though the server does elsewhere — see
/// <see cref="AccountExportService"/>); it encrypts it at rest via <see cref="ISnapshotCipher"/>.
/// </summary>
public sealed class SnapshotService(FinAppDbContext db, ISnapshotCipher cipher, ILogger<SnapshotService> log)
{
    public async Task<AccountSnapshot> GetAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var row = await db.AccountSnapshots.FindAsync([accountId], ct);
        var payload = row is null ? "" : await cipher.UnprotectAsync(row.Payload, ct);
        return new AccountSnapshot(accountId, row?.Version ?? 0, payload);
    }

    public async Task<long> SaveAsync(Guid userId, Guid accountId, SaveAccountRequest request, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        if (string.IsNullOrEmpty(request.Payload))
            throw new BadRequestException("Snapshot payload is required.");

        // Timed to split the server's share of a save: ProtectAsync wraps a fresh data key via KMS on EVERY write,
        // which is a network round-trip to another service — so it's a fixed per-save cost that chunking storage
        // would not reduce. Logged next to the write and payload size so the two are comparable.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stored = await cipher.ProtectAsync(request.Payload, ct);   // encrypt at rest (no-op in dev/no-KMS)
        var protectMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var row = await db.AccountSnapshots.FindAsync([accountId], ct);
        if (row is null)
        {
            if (request.ExpectedVersion != 0)
                throw new ConflictException("Snapshot is new (version 0).");
            row = new AccountSnapshotRow { AccountId = accountId, Version = 1, Payload = stored, UpdatedAt = DateTimeOffset.UtcNow };
            db.AccountSnapshots.Add(row);
        }
        else
        {
            if (row.Version != request.ExpectedVersion)
                throw new ConflictException($"Snapshot is at version {row.Version}; you sent {request.ExpectedVersion}. Reload and retry.");
            row.Version++;
            row.Payload = stored;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The manual check above catches a stale caller; this catches a concurrent write that committed between
            // our read and our save (the Version concurrency token makes the UPDATE match 0 rows). Same 409 either way.
            throw new ConflictException("This account changed while you were saving. Reload and retry.");
        }
        log.LogInformation(
            "[save] account={AccountId} payload={PayloadBytes}B stored={StoredBytes}B protect={ProtectMs}ms db={DbMs}ms v={Version}",
            accountId, request.Payload.Length, stored.Length, protectMs.ToString("0.#"), sw.Elapsed.TotalMilliseconds.ToString("0.#"), row.Version);
        return row.Version;
    }

    /// <summary>
    /// Initialize a freshly-created account's snapshot server-side: build the header (name/currency/owner/members)
    /// from the relational account, seed the shared starter body (<see cref="Account.SeedStarter"/>), anchor
    /// achievements to the first period, and save it as v1. The thin-client counterpart of the web app's first-load
    /// bootstrap — a native client can create an account without carrying the domain. Idempotent guard: fails with a
    /// 409 if a snapshot already exists. <paramref name="today"/> dates the first period (the caller's local month).
    /// </summary>
    public async Task<long> BootstrapAsync(Guid userId, Guid accountId, DateOnly today, CancellationToken ct = default)
    {
        // Load the relational account for its header only; its body collections are empty (the body lives in the
        // snapshot). Build a SEPARATE aggregate via CreateForHeader — never seed the EF-tracked entity, whose body
        // isn't mapped relationally.
        var relational = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (relational is null || !relational.IsContributor(userId))
            throw new NotFoundException("Account not found.");

        var existing = await db.AccountSnapshots.FindAsync([accountId], ct);
        if (existing is not null && !string.IsNullOrEmpty(existing.Payload))
            throw new ConflictException("This account is already set up.");

        var account = AccountSnapshotSerializer.CreateForHeader(
            relational.Id, relational.Name, relational.Currency, relational.OwnerUserId,
            relational.Members.Select(m => (m.UserId, m.DisplayName)));
        account.SeedStarter(today);
        if (account.CurrentPeriod is { } period)
            account.SetAchievementsAnchor(period.From);   // matches the web client's first-load anchor

        return await SaveAsync(userId, accountId, new SaveAccountRequest(AccountSnapshotSerializer.Serialize(account), 0), ct);
    }

    /// <summary>
    /// The server-side mutation spine for the Option-A migration (docs/MOBILE.md): load the snapshot (contributor
    /// auth + decrypt), deserialize the aggregate, apply <paramref name="mutate"/>, then serialize and save under the
    /// same optimistic concurrency a whole-snapshot PUT uses. This is the read-modify-write the client used to do
    /// locally, relocated so a thin (native) client can send just a command instead of the whole payload — and the
    /// money maths runs through the one domain, so it can't drift between clients.
    ///
    /// <para>Domain validation (<see cref="InvalidOperationException"/> / <see cref="ArgumentException"/> thrown by
    /// <paramref name="mutate"/>) surfaces as 400. Unlike a whole-snapshot save — where the caller owns the stale
    /// version and gets a 409 to reload — this owns the whole read-modify-write, so a concurrent write that lands
    /// mid-call is handled here: it reloads the winner's state and re-applies <paramref name="mutate"/>, up to a
    /// small retry budget (a 409 only after that's exhausted). <paramref name="mutate"/> must therefore be a pure
    /// function of the account it's handed — it can run more than once. Returns the new snapshot version and whatever
    /// <paramref name="mutate"/> yields (e.g. a newly-created entity's id).</para>
    /// </summary>
    public async Task<(long Version, T Result)> MutateAsync<T>(
        Guid userId, Guid accountId, Func<Account, T> mutate, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);

        var row = await db.AccountSnapshots.FindAsync([accountId], ct);
        if (row is null || string.IsNullOrEmpty(row.Payload))
            throw new BadRequestException("This account has no data to change yet.");

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // Work from the row's current state (freshened by ReloadAsync on a retry), applying the mutation to a
            // fresh aggregate each attempt so a re-run can't compound.
            var account = AccountSnapshotSerializer.Deserialize(await cipher.UnprotectAsync(row.Payload, ct));
            T result;
            try
            {
                result = mutate(account);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The domain guards invariants by throwing; translate to a clean 400 so a client sees the reason
                // (an unhandled throw would be masked as a 500). ApiExceptions from within mutate pass straight through.
                throw new BadRequestException(ex.CleanMessage());
            }

            row.Payload = await cipher.ProtectAsync(AccountSnapshotSerializer.Serialize(account), ct);
            row.Version++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);
                return (row.Version, result);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // Another writer bumped the row between our read and save. Pull their Payload + Version into the
                // tracked row and loop — the next pass re-applies the mutation on top of the winner's state.
                await db.Entry(row).ReloadAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("This account is being changed too rapidly. Reload and retry.");
            }
        }
    }

    /// <summary>
    /// The two-account counterpart of <see cref="MutateAsync{T}"/>, for cross-account writes (settlement / transfers
    /// on behalf of another account). Loads both snapshots (the caller must be a contributor on <b>both</b>), applies
    /// <paramref name="mutate"/> to the pair, and saves them together — EF batches both row UPDATEs in one transaction,
    /// so they commit atomically (either both land or neither does). Both carry the <c>Version</c> concurrency token, so
    /// a concurrent write to <em>either</em> account triggers the same reload-both-and-re-apply retry the single-account
    /// spine uses. <paramref name="mutate"/> must be a pure function of the two accounts it's handed (it can run more
    /// than once). Returns each account's new version and whatever <paramref name="mutate"/> yields.
    /// </summary>
    public async Task<(long PrimaryVersion, long SecondaryVersion, T Result)> MutateTwoAsync<T>(
        Guid userId, Guid primaryId, Guid secondaryId, Func<Account, Account, T> mutate, CancellationToken ct = default)
    {
        if (primaryId == secondaryId)
            throw new BadRequestException("The two accounts must be different.");
        await EnsureContributorAsync(userId, primaryId, ct);
        await EnsureContributorAsync(userId, secondaryId, ct);

        var primary = await db.AccountSnapshots.FindAsync([primaryId], ct);
        var secondary = await db.AccountSnapshots.FindAsync([secondaryId], ct);
        if (primary is null || string.IsNullOrEmpty(primary.Payload))
            throw new BadRequestException("This account has no data to change yet.");
        if (secondary is null || string.IsNullOrEmpty(secondary.Payload))
            throw new BadRequestException("The other account hasn't been opened yet.");

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            var a = AccountSnapshotSerializer.Deserialize(await cipher.UnprotectAsync(primary.Payload, ct));
            var b = AccountSnapshotSerializer.Deserialize(await cipher.UnprotectAsync(secondary.Payload, ct));
            T result;
            try
            {
                result = mutate(a, b);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new BadRequestException(ex.CleanMessage());
            }

            primary.Payload = await cipher.ProtectAsync(AccountSnapshotSerializer.Serialize(a), ct);
            primary.Version++;
            primary.UpdatedAt = DateTimeOffset.UtcNow;
            secondary.Payload = await cipher.ProtectAsync(AccountSnapshotSerializer.Serialize(b), ct);
            secondary.Version++;
            secondary.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);   // one transaction → both rows commit together, or neither
                return (primary.Version, secondary.Version, result);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // Whichever row lost the race (or both), pull fresh Payload+Version into the tracked entities and loop.
                await db.Entry(primary).ReloadAsync(ct);
                await db.Entry(secondary).ReloadAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("An account is being changed too rapidly. Reload and retry.");
            }
        }
    }

    /// <summary>One-off: encrypt any snapshot rows still stored as plaintext (only does work when a real cipher is
    /// configured). Idempotent — already-encrypted rows are skipped, and the content/version are unchanged. Returns
    /// how many rows were migrated.</summary>
    public async Task<int> EncryptLegacyRowsAsync(CancellationToken ct = default)
    {
        if (!cipher.Encrypts) return 0;
        // Both envelope versions count as already-encrypted. Missing one here would re-Protect a ciphertext string
        // — double-wrapping it into something no reader can open.
        var rows = await db.AccountSnapshots
            .Where(r => !r.Payload.StartsWith(EnvelopeSnapshotCipher.Prefix)
                     && !r.Payload.StartsWith(EnvelopeSnapshotCipher.PrefixGzip))
            .ToListAsync(ct);
        foreach (var row in rows)
            row.Payload = await cipher.ProtectAsync(row.Payload, ct);
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// The rows one source account holds against a trip in another account, with their names resolved (D1).
    /// <para>
    /// ⚠️⚠️ <b>This is the one read path in this service that does NOT check the caller's membership of the account
    /// it reads, and it is a deliberate exception in an otherwise universal discipline.</b> The alternative was
    /// gating the fan-out on the viewer: two people looking at the same shared trip would then see different
    /// totals — €1,200 for one partner and €900 for the other — which is precisely the class of "two figures about
    /// one number disagree" this codebase keeps having to fix. A trip total has to be a fact about the trip.
    /// </para>
    /// <para>
    /// What makes it acceptable is the shape, so keep the shape: the <c>Account</c> is deserialized inside this
    /// method and <b>never escapes it</b>. What comes back is only the expenses somebody in the source account
    /// deliberately attached to this exact trip, carrying only the fields a recap already renders — amount, date,
    /// category name, tag name, note. Never funds, members, balances or anything else in that snapshot. One call
    /// site (<c>TripsMap</c>); do not add a second without re-arguing the above.
    /// </para>
    /// <para>Returns empty rather than throwing for a source account that has gone, or holds nothing on this trip —
    /// the trip's directory of source accounts is a hint about where to look, never an authority.</para>
    /// </summary>
    public async Task<IReadOnlyList<ForeignTripExpense>> GetTripFanOutAsync(
        Guid sourceAccountId, Guid tripId, Guid tripAccountId, CancellationToken ct = default)
    {
        var header = await db.Accounts.FirstOrDefaultAsync(a => a.Id == sourceAccountId, ct);
        var row = await db.AccountSnapshots.FindAsync([sourceAccountId], ct);
        if (header is null || row is null || string.IsNullOrEmpty(row.Payload)) return [];

        Account source;
        try { source = AccountSnapshotSerializer.Deserialize(await cipher.UnprotectAsync(row.Payload, ct)); }
        catch (Exception ex)
        {
            // One unreadable source account must not take down the whole Trips screen of the account it fed.
            log.LogWarning(ex, "Trip fan-out: could not read source account {AccountId}", sourceAccountId);
            return [];
        }

        return source.ExpensesOnForeignTrip(tripId, tripAccountId)
            .Select(e => new ForeignTripExpense(
                sourceAccountId,
                header.Name,
                e,
                source.FindCategory(e.CategoryId)?.Name ?? "—",
                e.TagId is { } t ? source.FindTag(t)?.Name : null))
            .ToList();
    }

    private async Task EnsureContributorAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
    }
}
