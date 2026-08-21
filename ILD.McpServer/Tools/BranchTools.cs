using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that act on a work item's run branch with the orchestrator's
/// repository credentials. The agent runs under its own, lower-trust uid and can
/// reach neither the repository token nor the git askpass helper (ADR-0014), so a
/// plain <c>git pull</c> in the worktree cannot authenticate — it asks the
/// orchestrator to do the remote half instead.
///
/// Drift warning: these names and shapes must stay in lockstep with the Pi
/// surface (<see cref="ILD.Data.ToolDescriptors"/>) and the agent-API endpoints
/// (<c>AgentController</c>) so the chat behaves the same whichever CLI backs it.
/// </summary>
[McpServerToolType]
public sealed class BranchTools
{
    private readonly IldClient _ild;

    public BranchTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "pull_branch")]
    [Description("Pull the latest changes from origin into this work item's run branch — fetches origin with ILD's repository credentials and rebases the worktree branch onto its own remote counterpart (origin/<branch>). Use this to pick up commits pushed to the branch after the run started; you cannot do it yourself, git in the worktree has no credentials. The fetch syncs every remote branch (all of origin/*, stale ones pruned), so afterwards you can read any branch locally with plain git — including the run's base branch, which the result also reports this branch's standing against in 'baseBranch', 'behindBase' and 'aheadOfBase' (null when not measured), so you can decide whether the base needs merging in. It does NOT merge or rebase onto the base for you. Returns an 'outcome' of Updated, AlreadyUpToDate (nothing new on origin), NoRemoteBranch (the branch was never pushed — nothing to pull, not an error), DirtyWorktree (commit your changes first; 'files' lists them), Conflict (the rebase was aborted and the branch left untouched; 'files' lists the conflicted paths to resolve) or RebaseRefused (git would not rebase at all — nothing to resolve, read 'message').")]
    public Task<string> PullBranch(
        [Description("Work item GUID (from the Chat Context).")] string workItemId)
        => _ild.PostJsonAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pull-branch", new { });
}
