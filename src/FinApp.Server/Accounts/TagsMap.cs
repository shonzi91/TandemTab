using FinApp.Contracts;
using FinApp.Domain.Accounts;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds the thin-client manage-tags read model (see <see cref="TagsViewDto"/>).
/// <para>
/// Deliberately <b>not</b> filtered to <c>ActiveTags</c>: this is the one surface that has to show archived tags,
/// because it is the only place they can be restored from.
/// </para>
/// </summary>
public static class TagsMap
{
    public static TagsViewDto View(Account account, long version)
    {
        // One pass over every expense in the account rather than a per-tag scan: a tag count is O(expenses), and
        // doing it inside the tag loop would make the whole read O(tags x expenses) for a list that is drawn once.
        var uses = new Dictionary<Guid, int>();
        foreach (var tagId in account.Periods.SelectMany(p => p.Expenses).SelectMany(e => e.TagIds))
            uses[tagId] = uses.GetValueOrDefault(tagId) + 1;

        var tags = account.Tags
            .OrderBy(t => t.IsArchived)                       // live labels first; the archive settles at the bottom
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TagRowDto(
                t.Id, t.Name, t.Icon,
                t.CategoryId,
                t.CategoryId is { } cid ? account.FindCategory(cid)?.Name : null,
                t.IsTripTag, t.IsArchived,
                uses.GetValueOrDefault(t.Id)))
            .ToList();

        var categories = account.Categories
            .Where(c => !c.IsArchived)
            .Select(c => new CategoryOptionDto(c.Id, c.Name, c.Icon, c.ParentId))
            .ToList();

        return new TagsViewDto(version, tags, categories);
    }
}
