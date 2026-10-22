namespace ILD.Core.Services.Remote;

/// <summary>
/// The board order: the lowest <see cref="RemoteWorkItem.Priority"/> first,
/// equal priorities by id, shorter first and then ordinal, which is numeric
/// order for the built-in server's ids. The same order the WorkItem server
/// lists and polls in, so every reader of the board sees the same order.
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
