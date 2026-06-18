using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// In-memory record of one composed combined preview: the integration branch,
/// its worktree, the base repo it was cut from, and the branch each member was
/// merged at (the snapshot that staleness is computed against). Mutated only
/// under the owning service's per-key lock.
/// </summary>
public sealed class CombinedPreviewEntry
{
    public required string Key { get; init; }
    public required string IntegrationBranch { get; init; }
    public required string WorktreePath { get; init; }
    public required string BaseRepoPath { get; init; }
    public required Guid RepositoryId { get; init; }
    public List<CombinedPreviewMemberRecord> Members { get; init; } = new();
}

public sealed class CombinedPreviewMemberRecord
{
    public required string WorkItemId { get; init; }
    public string? Title { get; set; }

    /// <summary>The branch that was merged for this member when the preview was composed.</summary>
    public string? MergedBranch { get; set; }
    public string MergeStatus { get; set; } = "pending";
    public List<string> ConflictedFiles { get; set; } = new();
}

/// <summary>
/// Process-wide registry of live combined previews keyed by their member-id key
/// (<see cref="CombinedPreviewNaming.KeyFor"/>). Singleton so the scoped
/// <see cref="ICombinedPreviewService"/> sees the same set across requests.
/// </summary>
public sealed class CombinedPreviewRegistry
{
    private readonly ConcurrentDictionary<string, CombinedPreviewEntry> _entries = new(StringComparer.Ordinal);

    public CombinedPreviewEntry? Get(string key)
        => _entries.TryGetValue(key, out var entry) ? entry : null;

    public void Set(CombinedPreviewEntry entry)
        => _entries[entry.Key] = entry;

    public void Remove(string key)
        => _entries.TryRemove(key, out _);
}
