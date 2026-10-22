namespace ILD.Core.Services.Remote;

/// <summary>
/// The board order: the lowest <see cref="RemoteWorkItem.Priority"/> first,
/// equal priorities by id, shorter first and then ordinal, which is numeric
/// order for the built-in server's ids. The WorkItem server's
/// <c>WorkItemService.InBoardOrder</c> is the source of truth, and the taskboard's
/// <c>compareServerOrder</c> follows it too: change all three together.
/// </summary>
public static class WorkItemOrder
{
    public static IComparer<RemoteWorkItem> Board { get; } = Comparer<RemoteWorkItem>.Create((x, y) =>
    {
        var byPriority = x.Priority.CompareTo(y.Priority);
        if (byPriority != 0) return byPriority;
        var byLength = x.Id.Length.CompareTo(y.Id.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(x.Id, y.Id);
    });
}
