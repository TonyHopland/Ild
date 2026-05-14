using System.Collections.Concurrent;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Tracks the work items this ILD instance currently considers active
/// (Running / HumanFeedback / WaitingForIld). Used as the heartbeat input on
/// every poll and as the recovery seed on startup. The default
/// implementation is in-memory; a SQLite-backed implementation lands when
/// the hard switch removes the local WorkItemManager.
/// </summary>
public interface IActiveWorkItemTracker
{
    IReadOnlyList<string> Snapshot();
    void Add(string workItemId);
    void Remove(string workItemId);
    int Count { get; }
}

public sealed class InMemoryActiveWorkItemTracker : IActiveWorkItemTracker
{
    private readonly ConcurrentDictionary<string, byte> _ids = new();

    public IReadOnlyList<string> Snapshot() => _ids.Keys.ToList();
    public void Add(string id) => _ids.TryAdd(id, 0);
    public void Remove(string id) => _ids.TryRemove(id, out _);
    public int Count => _ids.Count;
}
