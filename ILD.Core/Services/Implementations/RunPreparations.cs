using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The work items whose run is having its container or pod made, from the moment
/// the run host starts creating it until the first command can run in it. Held in
/// memory only: a restart restarts the Start node, which marks it again.
/// </summary>
public sealed class RunPreparations
{
    private readonly ConcurrentDictionary<string, byte> _preparing = new(StringComparer.Ordinal);

    public void Begin(string workItemId) => _preparing[workItemId] = 0;

    public void End(string workItemId) => _preparing.TryRemove(workItemId, out _);

    public bool IsPreparing(string workItemId) => _preparing.ContainsKey(workItemId);
}
