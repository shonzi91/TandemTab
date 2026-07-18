using FinApp.Contracts;
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

        await db.SaveChangesAsync(ct);
        log.LogInformation(
            "[save] account={AccountId} payload={PayloadBytes}B stored={StoredBytes}B protect={ProtectMs}ms db={DbMs}ms v={Version}",
            accountId, request.Payload.Length, stored.Length, protectMs.ToString("0.#"), sw.Elapsed.TotalMilliseconds.ToString("0.#"), row.Version);
        return row.Version;
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

    private async Task EnsureContributorAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
    }
}
