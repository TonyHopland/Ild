namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// Saved adapter session transcripts: metadata rows plus raw content stored as
/// compressed chunks that each end on a line. Listing reads metadata only; content
/// is streamed a chunk at a time and never held whole.
///
/// A row written before the split still holds its transcript in the legacy column
/// until the startup content migration reaches it. Every read that needs the content
/// or its exact metadata converts such a row first; listing never does, and reports it
/// as <see cref="SessionSnapshotMetadata.Unconverted"/>.
/// </summary>
public interface IAdapterSessionSnapshotStore
{
    /// <summary>A run's snapshots, newest first (by last update, else creation), then by adapter and session id.</summary>
    Task<IReadOnlyList<SessionSnapshotMetadata>> ListMetadataForRunAsync(Guid loopRunId, CancellationToken cancellationToken = default);

    /// <summary>The snapshot's metadata, or null when there is none.</summary>
    Task<SessionSnapshotMetadata?> GetMetadataAsync(SessionSnapshotKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="newRaw"/> after the <paramref name="expectedByteLength"/> bytes
    /// already stored, creating the snapshot when it is missing and the expected length is 0.
    /// Writes nothing and returns <see cref="SessionSnapshotAppendResult.Conflict"/> when the
    /// stored length differs, or the row has not been converted yet.
    /// </summary>
    /// <param name="newTailHash">The tail hash of the content once appended (see <c>SessionSnapshotFormats.TailHash</c>).</param>
    Task<SessionSnapshotAppendResult> AppendAsync(
        SessionSnapshotKey key, string format, Stream newRaw, long expectedByteLength, string newTailHash,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces whatever is stored, legacy content included, with <paramref name="fullRaw"/> in one transaction.</summary>
    Task ReplaceAsync(SessionSnapshotKey key, string format, Stream fullRaw, CancellationToken cancellationToken = default);

    /// <summary>The raw content from <paramref name="fromRawOffset"/> on, read a chunk at a time; null when there is no snapshot.</summary>
    Task<Stream?> OpenReadAsync(SessionSnapshotKey key, long fromRawOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>The last <paramref name="maxEvents"/> lines; see the overload taking an end offset.</summary>
    Task<SessionSnapshotPage> ReadTailAsync(SessionSnapshotKey key, int maxEvents, CancellationToken cancellationToken = default)
        => ReadTailAsync(key, maxEvents, null, cancellationToken);

    /// <summary>
    /// Up to <paramref name="maxEvents"/> lines ending at <paramref name="beforeRawOffset"/> (the end of
    /// the content when null), reading only the chunks they are in and never a line whose start it has
    /// not read; an empty page when there is no snapshot. Its <see cref="SessionSnapshotPage.PreviousOffset"/>
    /// is where to ask for the page before it.
    /// </summary>
    Task<SessionSnapshotPage> ReadTailAsync(SessionSnapshotKey key, int maxEvents, long? beforeRawOffset, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="maxEvents"/> lines from <paramref name="fromRawOffset"/>, reading no chunk before it; an empty page when there is no snapshot.</summary>
    Task<SessionSnapshotPage> ReadRangeAsync(SessionSnapshotKey key, long fromRawOffset, int maxEvents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the source snapshot to <paramref name="destination"/> with every occurrence of the
    /// source session id replaced by the destination's, leaving the source untouched.
    /// False, writing nothing, when the source has no snapshot.
    /// </summary>
    Task<bool> ForkAsync(SessionSnapshotKey source, SessionSnapshotKey destination, CancellationToken cancellationToken = default);
}

/// <summary>Which snapshot: its owner (a run or a chat), adapter and session id.</summary>
public sealed record SessionSnapshotKey(Guid? LoopRunId, Guid? ChatSessionId, string AdapterName, string SessionId)
{
    public static SessionSnapshotKey ForRun(Guid loopRunId, string adapterName, string sessionId)
        => new(loopRunId, null, adapterName, sessionId);

    public static SessionSnapshotKey ForChat(Guid chatSessionId, string adapterName, string sessionId)
        => new(null, chatSessionId, adapterName, sessionId);
}

/// <summary>
/// A snapshot's metadata. <see cref="Unconverted"/> rows still hold their content in the
/// legacy column, so their format, lengths and counts are not known yet.
/// </summary>
public sealed record SessionSnapshotMetadata(
    Guid Id,
    Guid? LoopRunId,
    Guid? ChatSessionId,
    string AdapterName,
    string SessionId,
    string Format,
    string Compression,
    long ByteLength,
    int ChunkCount,
    int EventCount,
    string? TailHash,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    bool Unconverted);

public enum SessionSnapshotAppendResult
{
    Appended,
    Conflict,
}

/// <summary>
/// Lines of a snapshot, each without its '\n': the first starts at raw offset
/// <see cref="FromOffset"/>; <see cref="NextOffset"/> is where the next page starts, or
/// null when the page reaches the end of the content; <see cref="PreviousOffset"/> is where
/// the page before it ends, or null when it starts the content. A page holds about a
/// megabyte of lines at most; a single line longer than that is cut off at it, and
/// <see cref="Truncated"/> says so.
/// </summary>
public sealed record SessionSnapshotPage(IReadOnlyList<string> Events, long FromOffset, long? NextOffset, long? PreviousOffset, bool Truncated);
