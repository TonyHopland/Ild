namespace ILD.Data.Stores;

/// <summary>
/// A conversation event was refused because the run has a terminal status or
/// a run-ending event. Nothing more may be said on it.
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
