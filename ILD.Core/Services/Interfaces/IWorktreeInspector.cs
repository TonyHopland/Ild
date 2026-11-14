namespace ILD.Core.Services.Interfaces;

public enum WorktreeReadOutcome
{
    Ok,
    WorkItemNotFound,
    NoWorktree,
    Invalid,
    NotFound,
    Failed,
}

/// <summary>
/// One read of a work item's worktree. <see cref="Text"/> is already capped: when <see cref="Truncated"/>, it ends in a
/// marker naming how many more bytes there were.
/// </summary>
public sealed record WorktreeReadResult(
    WorktreeReadOutcome Outcome,
    string? Message = null,
    string Text = "",
    bool Truncated = false,
    long OmittedBytes = 0)
{
    public bool Ok => Outcome == WorktreeReadOutcome.Ok;
}

/// <summary>
/// Read-only questions about a work item's run worktree, uncommitted changes included. Every answer comes from a fixed
/// git command run agent-scoped without a shell, or a bounded read of one working-tree file; nothing writes, nothing
/// reaches the network, and a path that leaves the worktree or enters <c>.git</c> is refused. See ADR-0038.
/// </summary>
public interface IWorktreeInspector
{
    /// <summary><c>git status --porcelain</c>: changed and untracked files.</summary>
    Task<WorktreeReadResult> GetStatusAsync(string workItemId, CancellationToken ct = default);

    /// <summary>The working tree against the run's fork point, optionally for one path or as a stat only. An untracked path is shown as all added.</summary>
    Task<WorktreeReadResult> GetDiffAsync(string workItemId, string? path, bool statOnly, CancellationToken ct = default);

    /// <summary>One working-tree file, text only and capped.</summary>
    Task<WorktreeReadResult> ReadFileAsync(string workItemId, string path, CancellationToken ct = default);

    /// <summary>Lines matching <paramref name="pattern"/> (a fixed string unless <paramref name="regex"/>) in tracked and untracked files, optionally below <paramref name="path"/>.</summary>
    Task<WorktreeReadResult> SearchAsync(string workItemId, string pattern, bool regex, string? path, CancellationToken ct = default);

    /// <summary>The newest commits the run added on top of its fork point.</summary>
    Task<WorktreeReadResult> GetLogAsync(string workItemId, int limit, CancellationToken ct = default);

    /// <summary>One commit's message and patch; it has to be in the history of the run branch.</summary>
    Task<WorktreeReadResult> GetCommitAsync(string workItemId, string sha, CancellationToken ct = default);
}
