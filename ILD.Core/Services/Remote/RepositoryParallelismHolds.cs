namespace ILD.Core.Services.Remote;

/// <summary>
/// Why a Ready work item was not claimed: its repository already had
/// <see cref="InProgress"/> items holding slots against a limit of <see cref="Limit"/>.
/// </summary>
public sealed record RepositoryParallelismHold(int InProgress, int Limit);

/// <summary>
/// The Ready work items the latest scheduler pass held back by their
/// repository's parallelism limit, keyed by work item id. Written only at the
/// end of a pass by <see cref="RemoteWorkItemCoordinator"/> (passes run one at
/// a time), read by the board through an immutable snapshot, so readers never
/// see a half-written pass.
/// </summary>
public sealed class RepositoryParallelismHolds
{
    private static readonly IReadOnlyDictionary<string, RepositoryParallelismHold> Empty =
        new Dictionary<string, RepositoryParallelismHold>(StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<string, RepositoryParallelismHold> _holds = Empty;

    public RepositoryParallelismHold? Get(string workItemId)
        => _holds.TryGetValue(workItemId, out var hold) ? hold : null;

    /// <summary>
    /// Swap in a pass's holds and return the ids whose hold appeared,
    /// disappeared or changed counts since the previous pass.
    /// </summary>
    public IReadOnlyList<string> Replace(IReadOnlyDictionary<string, RepositoryParallelismHold> next)
    {
        var previous = _holds;
        _holds = next;
        return previous.Keys.Union(next.Keys, StringComparer.Ordinal)
            .Where(id => !Equals(previous.GetValueOrDefault(id), next.GetValueOrDefault(id)))
            .ToList();
    }

    public void Clear() => _holds = Empty;
}
