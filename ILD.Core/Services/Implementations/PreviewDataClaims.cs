using System.Diagnostics.CodeAnalysis;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Which previews hold each repository's preview data. Every preview start or
/// install that knows its repository takes a <see cref="Lease"/> for its worktree,
/// whatever the repository's Keep preview data setting, and keeps it for as long as
/// it runs. A keep-on start is refused while a lease of another worktree exists,
/// because keep-on previews share one folder; a keep-off start is never refused. A
/// reset of the repository's preview data is refused while any lease exists, and
/// every start is refused while a reset runs.
/// <para>
/// One instance per process, shared by the preview service and the reset, and kept
/// only in memory: nothing outlives a restart, and neither does any preview.
/// </para>
/// </summary>
public sealed class PreviewDataClaims
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<Lease>> _leases = new();
    private readonly HashSet<Guid> _resetting = new();

    /// <summary>
    /// Records that <paramref name="worktreePath"/> holds the preview data of
    /// <paramref name="scope"/>'s repository, or says why it may not: what holds it
    /// instead, or that it is being reset.
    /// </summary>
    public bool TryAcquire(
        PreviewVolumeScope scope, string worktreePath,
        [NotNullWhen(true)] out Lease? acquired, [NotNullWhen(false)] out PreviewDataRefusal? refusal)
    {
        acquired = null;
        lock (_lock)
        {
            if (_resetting.Contains(scope.RepositoryId))
            {
                refusal = Resetting;
                return false;
            }

            var leases = _leases.GetValueOrDefault(scope.RepositoryId);
            if (scope.KeepData && leases?.FirstOrDefault(l => l.Holder.WorktreePath != worktreePath) is { } held)
            {
                refusal = HeldBy(held.Holder);
                return false;
            }

            var lease = new Lease(this, scope.RepositoryId, new PreviewDataHolder(worktreePath, scope.WorkItemId, scope.WorkItemTitle));
            if (leases is null)
                _leases[scope.RepositoryId] = leases = [];
            leases.Add(lease);
            acquired = lease;
            refusal = null;
            return true;
        }
    }

    /// <summary>
    /// What <see cref="TryAcquire"/> would refuse now, without acquiring anything, so a start
    /// that runs on in the background can refuse at once. A holder from the same work item is
    /// not a refusal: the start takes its preview over.
    /// </summary>
    public PreviewDataRefusal? RefusalFor(PreviewVolumeScope scope, string worktreePath)
    {
        lock (_lock)
        {
            if (_resetting.Contains(scope.RepositoryId))
                return Resetting;

            var held = scope.KeepData
                ? _leases.GetValueOrDefault(scope.RepositoryId)?.FirstOrDefault(l =>
                    l.Holder.WorktreePath != worktreePath && l.Holder.WorkItemId != scope.WorkItemId)
                : null;
            return held is null ? null : HeldBy(held.Holder);
        }
    }

    private static readonly PreviewDataRefusal Resetting =
        new("The preview data of this repository is being reset. Try again when it finishes.", null);

    private static PreviewDataRefusal HeldBy(PreviewDataHolder holder)
        => new($"{holder.Sentence} Stop it before starting this one.", holder);

    /// <summary>
    /// Marks the repository's preview data as being reset, so no preview starts until
    /// <see cref="EndReset"/>; refused while a preview holds it or another reset runs.
    /// </summary>
    public bool TryBeginReset(Guid repositoryId, [NotNullWhen(false)] out string? refusal)
    {
        lock (_lock)
        {
            if (_leases.GetValueOrDefault(repositoryId)?.FirstOrDefault() is { } held)
            {
                refusal = $"{held.Holder.Sentence} Stop it before removing the preview data.";
                return false;
            }

            if (!_resetting.Add(repositoryId))
            {
                refusal = "The preview data of this repository is already being removed.";
                return false;
            }

            refusal = null;
            return true;
        }
    }

    public void EndReset(Guid repositoryId)
    {
        lock (_lock)
            _resetting.Remove(repositoryId);
    }

    private void Release(Lease lease)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(lease.RepositoryId, out var leases))
                return;
            leases.Remove(lease);
            if (leases.Count == 0)
                _leases.Remove(lease.RepositoryId);
        }
    }

    /// <summary>One preview's hold on its repository's preview data; disposing it lets go, once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly PreviewDataClaims _owner;
        private int _released;

        internal Lease(PreviewDataClaims owner, Guid repositoryId, PreviewDataHolder holder)
        {
            _owner = owner;
            RepositoryId = repositoryId;
            Holder = holder;
        }

        public Guid RepositoryId { get; }
        public PreviewDataHolder Holder { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(this);
        }
    }
}

/// <summary>The preview, by worktree and work item, that holds a repository's preview data.</summary>
public sealed record PreviewDataHolder(string WorktreePath, string WorkItemId, string WorkItemTitle)
{
    public string Sentence => $"A preview is already running for work item {WorkItemId} ({WorkItemTitle}).";
}

/// <summary>
/// Why a preview may not start: <see cref="Holder"/> holds the repository's preview
/// data, or, without one, it is being reset.
/// </summary>
public sealed record PreviewDataRefusal(string Message, PreviewDataHolder? Holder);
