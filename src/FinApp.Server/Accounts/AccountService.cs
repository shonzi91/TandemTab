using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Accounts;

/// <summary>
/// Server-authoritative CRUD for domain accounts, scoped to the calling user. A user sees accounts they
/// are a contributor of (owned or joined). Rename/delete are owner-only; everything else inside an account
/// is editable by any contributor (enforced by the per-resource endpoints/snapshots).
/// </summary>
public sealed class AccountService(FinAppDbContext db, ArchivedAccountsService archives)
{
    public async Task<List<AccountSummaryDto>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var accounts = await db.Accounts
            .Include(a => a.Members)
            .Where(a => a.Members.Any(m => m.UserId == userId))
            .ToListAsync(ct);

        var archived = await archives.ArchivedIdsAsync(ct);
        return accounts.Where(a => !archived.Contains(a.Id)).Select(a => ToSummary(a, userId)).ToList();
    }

    /// <summary>Archived accounts the user is (still) a member of, with when each was archived + the purge deadline.</summary>
    public async Task<List<ArchivedAccountDto>> ListArchivedForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var accounts = await db.Accounts
            .Include(a => a.Members)
            .Where(a => a.Members.Any(m => m.UserId == userId))
            .ToListAsync(ct);

        var archivedAt = await archives.ArchivedAtAsync(ct);
        return accounts
            .Where(a => archivedAt.ContainsKey(a.Id))
            .Select(a => new ArchivedAccountDto(a.Id, a.Name, a.Currency, archivedAt[a.Id],
                archivedAt[a.Id].AddDays(ArchivedAccountsService.RetentionDays)))
            .ToList();
    }

    /// <summary>Leave an account. If the caller is the sole member the account is archived (soft-deleted for a
    /// grace period); if they own it and others remain, ownership must first pass to <paramref name="newOwnerUserId"/>.</summary>
    public async Task<LeaveAccountResult> LeaveAsync(Guid userId, Guid accountId, Guid? newOwnerUserId, CancellationToken ct = default)
    {
        var account = await LoadContributorAsync(userId, accountId, ct);

        if (account.Members.Count == 1)
        {
            await archives.ArchiveAsync(accountId, ct);
            return LeaveAccountResult.Archived;
        }

        if (account.IsOwner(userId))
        {
            if (newOwnerUserId is not { } newOwner || newOwner == userId)
                throw new BadRequestException("Choose who should take over the account before you leave.");
            try { account.TransferOwnership(newOwner); }
            catch (InvalidOperationException ex) { throw new BadRequestException(ex.Message); }
        }

        account.RemoveMember(userId);
        await db.SaveChangesAsync(ct);
        return LeaveAccountResult.Left;
    }

    /// <summary>Owner removes another member from the account.</summary>
    public async Task RemoveMemberAsync(Guid ownerUserId, Guid accountId, Guid targetUserId, CancellationToken ct = default)
    {
        var account = await LoadOwnedAsync(ownerUserId, accountId, ct);
        if (targetUserId == ownerUserId)
            throw new BadRequestException("Use “leave account” to remove yourself.");
        try { account.RemoveMember(targetUserId); }
        catch (InvalidOperationException ex) { throw new BadRequestException(ex.Message); }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Owner hands ownership to another member (without leaving).</summary>
    public async Task TransferOwnershipAsync(Guid ownerUserId, Guid accountId, Guid newOwnerUserId, CancellationToken ct = default)
    {
        var account = await LoadOwnedAsync(ownerUserId, accountId, ct);
        try { account.TransferOwnership(newOwnerUserId); }
        catch (InvalidOperationException ex) { throw new BadRequestException(ex.Message); }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Restore an archived account (within the grace window) back to the active list.</summary>
    public async Task ReactivateAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await LoadContributorAsync(userId, accountId, ct);   // must still be a member
        await archives.UnarchiveAsync(accountId, ct);
    }

    public async Task<AccountSummaryDto> CreateAsync(Guid userId, string displayName, CreateAccountRequest request, CancellationToken ct = default)
    {
        Account account;
        try
        {
            account = new Account(request.Name, request.Currency);
            account.AssignOwner(userId, string.IsNullOrWhiteSpace(displayName) ? "Me" : displayName);
        }
        catch (ArgumentException ex)
        {
            throw new BadRequestException(ex.CleanMessage());
        }

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return ToSummary(account, userId);
    }

    public async Task RenameAsync(Guid userId, Guid accountId, string name, CancellationToken ct = default)
    {
        var account = await LoadOwnedAsync(userId, accountId, ct);
        try
        {
            account.Rename(name);
        }
        catch (ArgumentException ex)
        {
            throw new BadRequestException(ex.CleanMessage());
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Delete" an account the caller owns — a soft delete: it's archived (hidden, recoverable for
    /// <see cref="ArchivedAccountsService.RetentionDays"/> days from the profile) and only then hard-deleted by
    /// the startup purge. Matches leaving as the sole member, so an accidental delete isn't instantly irreversible.</summary>
    public async Task DeleteAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        await LoadOwnedAsync(userId, accountId, ct);   // owner-only (403/404); account row stays until purged
        await archives.ArchiveAsync(accountId, ct);
    }

    /// <summary>Load an account the user is a member of, or 404.</summary>
    private async Task<Account> LoadContributorAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
        return account;
    }

    /// <summary>Load an account the user can see, or 404; throw 403 unless they own it (rename/delete gate).</summary>
    private async Task<Account> LoadOwnedAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts
            .Include(a => a.Members)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
        if (!account.IsOwner(userId))
            throw new ForbiddenException("Only the account owner can do that.");

        return account;
    }

    private static AccountSummaryDto ToSummary(Account a, Guid userId) =>
        new(a.Id, a.Name, a.Currency, a.OwnerUserId, a.IsOwner(userId),
            a.Members.Select(m => new MemberDto(m.UserId, m.DisplayName)).ToList());
}
