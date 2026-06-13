namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Point-in-time snapshot of a run's buffered live output. <see cref="Text"/>
/// is the full (possibly front-truncated) stream captured so far;
/// <see cref="LastSeq"/> is the sequence number of the last chunk included in
/// it, so a replaying client can drop live chunks it already has.
/// </summary>
public readonly record struct RunProgressSnapshot(string Text, long LastSeq);

/// <summary>
/// Bounded, in-memory capture of the complete live output produced per run.
/// Feeds both the live SignalR <c>NodeProgress</c> stream (via the sequence
/// number returned from <see cref="Append"/>) and the replay sent to clients
/// that join mid-run (via <see cref="Snapshot"/>). Implementations must be
/// safe to call from any thread and never throw.
/// </summary>
public interface IRunProgressBuffer
{
    /// <summary>
    /// Append a raw chunk to the run's buffer and return the monotonically
    /// increasing sequence number assigned to it. The sequence keeps counting
    /// even when older text is trimmed, so it stays a stable cursor.
    /// </summary>
    long Append(Guid runId, string chunk);

    /// <summary>Current buffered text and the last assigned sequence number.</summary>
    RunProgressSnapshot Snapshot(Guid runId);

    /// <summary>Drop the run's buffer (called when the run becomes terminal).</summary>
    void Clear(Guid runId);
}
