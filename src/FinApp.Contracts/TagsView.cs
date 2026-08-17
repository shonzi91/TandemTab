namespace FinApp.Contracts;

/// <summary>
/// One tag as the manage surface reads it — the stored fields plus the two things a manager needs that a picker
/// never does: whether it is archived, and how many expenses would be left pointing at nothing if it were removed.
/// </summary>
/// <param name="CategoryName">The F2 binding resolved to a name, so the row can say "→ Food" without the client
/// joining against a category list it may not have fetched. Null when the tag carries no filing opinion.</param>
/// <param name="Uses">How many expenses currently carry this tag. <c>RemoveTag</c> is a <b>hard</b> delete that
/// leaves those rows holding a dangling id, so this is the number that makes the confirm dialog mean something —
/// "Remove Lidl?" and "Remove Lidl, used on 84 expenses?" are different questions.</param>
public record TagRowDto(
    Guid Id,
    string Name,
    string? Icon,
    Guid? CategoryId,
    string? CategoryName,
    bool TripTag,
    bool Archived,
    int Uses);

/// <summary>
/// Every tag in the account, archived ones included — the read the manage-tags surface needs and the picker
/// deliberately must not use.
/// <para>
/// <b>★ Why this exists as its own read.</b> The only tag list a thin client could fetch was
/// <c>SpendingViewDto.Tags</c>, and that is built from <c>Account.ActiveTags</c> because it is the <i>picker</i>
/// source — an archived tag appearing there is the whole reason archiving exists. So a client working from it
/// could archive a tag and then never see it again: an archive that is really a delete, and a one-way door.
/// Two different questions ("what may I apply?" and "what do I own?") need two different reads; folding an
/// <c>Archived</c> flag into the picker's list would have answered them both wrongly.
/// </para>
/// </summary>
/// <param name="Categories">The category options for the F2 binding picker, so the manage surface is self-sufficient
/// — it is reachable without the Spending view having been loaded.</param>
public record TagsViewDto(long Version, IReadOnlyList<TagRowDto> Tags, IReadOnlyList<CategoryOptionDto> Categories)
{
    public static readonly TagsViewDto Empty = new(0, [], []);
}
