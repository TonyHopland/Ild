using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// What a start has reached, readable without a lock: the snapshot is replaced
/// wholesale on every change (as <see cref="PreviewRuntime"/> does its process list)
/// and never mutated once published, so the status endpoints and a start's concurrent
/// health waits never see a half-applied update.
/// </summary>
internal sealed class StartProgress
{
    private readonly object _writers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<WorktreePreviewResponse> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile WorktreePreviewResponse _snapshot;
    private int _joined;

    public StartProgress(WorktreePreviewResponse initial, string? workItemId, PreviewStartScope scope)
    {
        _snapshot = initial;
        WorkItemId = workItemId;
        Scope = scope;
    }

    public string? WorkItemId { get; }

    /// <summary>What this start brings up: a start asking for no more than that joins it instead of running its own.</summary>
    public PreviewStartScope Scope { get; }

    public WorktreePreviewResponse Snapshot => _snapshot;

    public bool InProgress => _snapshot.State is "installing" or "starting";

    public CancellationToken Stopping => _stop.Token;

    public void Stop() => _stop.Cancel();

    /// <summary>How the start ended, for a start that joined it instead of running its own.</summary>
    public Task<WorktreePreviewResponse> Outcome => _outcome.Task;

    public int Joined => Volatile.Read(ref _joined);

    public void Join() => Interlocked.Increment(ref _joined);

    public void Succeeded(WorktreePreviewResponse response) => _outcome.TrySetResult(response);

    public void Failed(Exception failure)
    {
        _outcome.TrySetException(failure is OperationCanceledException
            ? new InvalidOperationException("The preview start this one joined was cancelled before it finished.")
            : failure);
        // A start nobody joined leaves its failure unobserved; that is not an error.
        _ = _outcome.Task.Exception;
    }

    public void Update(Action<WorktreePreviewResponse> change)
    {
        lock (_writers)
        {
            var next = _snapshot.Clone();
            change(next);
            _snapshot = next;
        }
    }
}
