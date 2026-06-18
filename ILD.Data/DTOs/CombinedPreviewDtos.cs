namespace ILD.Data.DTOs;

/// <summary>
/// How the combined-preview composer reacts when merging a member branch hits a
/// conflict. A conflict is treated as signal, not failure — the merge always
/// halts at the first one and reports which member/files collided; the mode only
/// decides what is left behind in the throwaway integration worktree.
/// </summary>
public enum CombinedPreviewConflictMode
{
    /// <summary>Abort the conflicting merge and report it, leaving a clean worktree.</summary>
    Stop = 0,

    /// <summary>Leave the conflict markers in the worktree for a human to fix and commit there.</summary>
    ResolveInWorktree = 1,
}

public sealed class CombinedPreviewStartRequest
{
    /// <summary>The work items to compose, in selection order (used as a tie-break for merge order).</summary>
    public List<string> WorkItemIds { get; set; } = new();

    /// <summary>Members to exclude from the merge ("skip this branch"); the rest preview as a partial set.</summary>
    public List<string>? Skip { get; set; }

    /// <summary>What to do with the integration worktree on the first merge conflict. Defaults to <c>Stop</c>.</summary>
    public CombinedPreviewConflictMode OnConflict { get; set; } = CombinedPreviewConflictMode.Stop;

    // Forwarded to the underlying worktree preview once the merge composes cleanly.
    public string? ProfileName { get; set; }
    public bool SkipInstall { get; set; }
    public string? PublicHost { get; set; }
    public Dictionary<string, int>? PortOverrides { get; set; }
}

public sealed class CombinedPreviewRequest
{
    public List<string> WorkItemIds { get; set; } = new();
}

/// <summary>
/// One member of a combined preview together with how its branch fared in the
/// merge. <see cref="MergeStatus"/> is one of <c>pending</c>, <c>clean</c>,
/// <c>conflict</c>, <c>skipped</c>, or <c>missing</c> (no resolvable run branch).
/// </summary>
public sealed class CombinedPreviewMemberResponse
{
    public string WorkItemId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? BranchName { get; set; }
    public string MergeStatus { get; set; } = "pending";
    public List<string> ConflictedFiles { get; set; } = new();

    /// <summary>True when the member has re-run since this preview was composed (its current branch differs).</summary>
    public bool Stale { get; set; }
}

public sealed class CombinedPreviewResponse
{
    /// <summary>The deterministic integration branch name (<c>ild/combined-&lt;sorted-ids&gt;</c>).</summary>
    public string IntegrationBranch { get; set; } = string.Empty;

    /// <summary>
    /// <c>notStarted</c>, <c>running</c>, <c>partial</c> (running with skipped members),
    /// <c>conflict</c>, or <c>stopped</c>.
    /// </summary>
    public string State { get; set; } = "notStarted";

    /// <summary>True when any composed member has re-run since the preview was built; user rebuilds to refresh.</summary>
    public bool Stale { get; set; }

    /// <summary>
    /// True when a conflict left markers in the integration worktree for manual
    /// resolution; the human fixes and commits there, then resumes to continue.
    /// </summary>
    public bool AwaitingResolution { get; set; }

    public string? WorktreePath { get; set; }
    public string? Message { get; set; }

    public List<CombinedPreviewMemberResponse> Members { get; set; } = new();

    /// <summary>The underlying worktree preview (services/ports/URLs) once the set is running; null otherwise.</summary>
    public WorktreePreviewResponse? Preview { get; set; }
}
