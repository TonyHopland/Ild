namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Settings &gt; Storage: how much disk ILD's data takes, and the bulk Run Cleanup
/// of worktrees whose work items are Done. Sizes are allocated bytes, each file
/// counted once; null means not measured (the platform cannot report it, or the
/// path is not ILD's alone), never an estimate. No server path is ever returned.
/// </summary>
public interface IStorageService
{
    Task<StorageOverview> GetOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every run whose worktree the cleanup would remove: finished, not pinned,
    /// its work item Done on the WorkItem Server, and no run of that work item
    /// active. Throws when the server cannot be reached (see <see cref="Remote.WorkItemServerUnreachable"/>).
    /// </summary>
    Task<WorktreeCleanupCandidates> GetCleanupCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Run Cleanup for each of <paramref name="runIds"/> still a candidate when
    /// its turn comes, re-read just before acting; nothing else is touched.
    /// Throws when the WorkItem Server cannot be reached (see
    /// <see cref="Remote.WorkItemServerUnreachable"/>) before any run was acted on;
    /// after that, the runs left are reported Skipped.
    /// </summary>
    Task<WorktreeCleanupResult> CleanUpWorktreesAsync(IReadOnlyCollection<Guid> runIds);
}

public sealed record StorageOverview(IReadOnlyList<StorageCategory> Categories);

public sealed record StorageCategory(string Key, string Label, long? Bytes, bool Incomplete, IReadOnlyList<StorageEntry> Entries);

public sealed record StorageEntry(string Key, string Label, Guid? RepositoryId, string? RepositoryName, long? Bytes, bool Incomplete);

public sealed record WorktreeCleanupCandidates(IReadOnlyList<WorktreeCleanupCandidate> Candidates, long? TotalBytes);

public sealed record WorktreeCleanupCandidate(
    Guid RunId, string WorkItemId, string WorkItemTitle, Guid? RepositoryId, string? RepositoryName, long? Bytes, bool Incomplete);

public enum WorktreeCleanupOutcome
{
    Cleaned,
    Skipped,
    Failed,
}

public sealed record WorktreeCleanupRunResult(Guid RunId, WorktreeCleanupOutcome Outcome, long? Bytes, string? Reason);

public sealed record WorktreeCleanupResult(IReadOnlyList<WorktreeCleanupRunResult> Results, long? TotalBytes);
