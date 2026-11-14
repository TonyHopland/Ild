using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// Read-only MCP tools over a work item's run worktree, uncommitted changes included. ILD runs a fixed set of git
/// commands, or one bounded file read, agent-scoped and without a shell; nothing can be written or executed through
/// them, and a path outside the worktree or inside <c>.git</c> is refused. Every answer is
/// <c>{text, truncated, omittedBytes}</c>: when truncated, the text ends in a marker saying how much more there was.
///
/// Drift warning: these shapes must stay in lockstep with the agent-API endpoints
/// (<c>AgentController</c>) they call.
/// </summary>
[McpServerToolType]
public sealed class WorktreeTools
{
    private readonly IldClient _ild;

    public WorktreeTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_worktree_status")]
    [Description("List the files a work item's worktree has changed or added but not yet committed (git status --porcelain: two status letters, then the path; '??' is an untracked file). Works while the run is in progress. Start here to answer 'what changed?', then read the diff. Returns {text, truncated, omittedBytes}; an error says when the work item has no worktree.")]
    public Task<string> GetWorktreeStatus(
        [Description("Work item GUID (from the Chat Context).")] string workItemId)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/status");

    [McpServerTool(Name = "get_worktree_diff")]
    [Description("The work item worktree's changes against the commit its run branched from, committed and uncommitted together, as a unified diff. Pass a path for one file (an untracked file is shown as entirely added); untracked files are otherwise not in the whole diff, so use get_worktree_status to see them. A large diff is cut off with a marker: call again with statOnly for the per-file summary, then with a path for each file. Returns {text, truncated, omittedBytes}.")]
    public Task<string> GetWorktreeDiff(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Optional path relative to the worktree root, to diff one file or folder.")] string? path = null,
        [Description("Return only the per-file change summary (git diff --stat) instead of the patch.")] bool statOnly = false)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/diff?statOnly={statOnly.ToString().ToLowerInvariant()}"
            + (path is null ? "" : $"&path={Uri.EscapeDataString(path)}"));

    [McpServerTool(Name = "read_worktree_file")]
    [Description("Read one text file from a work item's worktree as it is on disk now, uncommitted edits included. The path is relative to the worktree root; it may not leave the worktree or enter .git. A binary file is reported by size, not shown, and a long file is cut off with a marker. Returns {text, truncated, omittedBytes}.")]
    public Task<string> ReadWorktreeFile(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Path relative to the worktree root, e.g. 'src/app.ts'.")] string path)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/file?path={Uri.EscapeDataString(path)}");

    [McpServerTool(Name = "search_worktree")]
    [Description("Search a work item's worktree for a string (git grep, tracked and untracked files, .gitignore honoured, binary files skipped). Returns matching lines as 'path:line:text' in {text, truncated, omittedBytes}; 'No matches.' when none. The pattern is a fixed string unless regex is true (extended regular expression).")]
    public Task<string> SearchWorktree(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("What to look for (up to 500 characters).")] string pattern,
        [Description("Treat the pattern as an extended regular expression (default false: a fixed string).")] bool regex = false,
        [Description("Optional path relative to the worktree root to search only there.")] string? path = null)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/search?pattern={Uri.EscapeDataString(pattern)}"
            + $"&regex={regex.ToString().ToLowerInvariant()}"
            + (path is null ? "" : $"&path={Uri.EscapeDataString(path)}"));

    [McpServerTool(Name = "get_worktree_log")]
    [Description("The commits a work item's run has added to its branch, newest first, one per line: full sha, date, author, subject. Does not include uncommitted work (see get_worktree_status). Returns {text, truncated, omittedBytes}.")]
    public Task<string> GetWorktreeLog(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("How many commits to list (1 to 100, default 20).")] int limit = 20)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/log?limit={limit}");

    [McpServerTool(Name = "get_worktree_commit")]
    [Description("One commit of a work item's run branch: its message and patch. Takes a sha from get_worktree_log (7 to 64 lowercase hex characters); a commit that is not in the branch's history is refused. Returns {text, truncated, omittedBytes}.")]
    public Task<string> GetWorktreeCommit(
        [Description("Work item GUID (from the Chat Context).")] string workItemId,
        [Description("Commit sha, as listed by get_worktree_log.")] string sha)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/worktree/commits/{Uri.EscapeDataString(sha)}");
}
