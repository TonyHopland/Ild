namespace ILD.Data.Stores;

/// <summary>
/// A conversation event was appended to a run whose run-ending event is already
/// written. The run is closed: nothing more may be said on it.
/// </summary>
public sealed class RunClosedException : InvalidOperationException
{
    public RunClosedException(Guid runId)
        : base($"Run {runId} has ended; nothing more can be added to its conversation.")
    {
        RunId = runId;
    }

    public Guid RunId { get; }
}
