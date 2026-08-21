using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

public interface IWorkItemManager
{
    Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId);
    Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId, Guid? createdByLoopRunId, bool forceBacklog, IEnumerable<string>? tags = null, Guid? createdByChatSessionId = null, string? branchNameOverride = null, string? baseBranchOverride = null);

    /// <summary>
    /// Edit the server-held fields of a work item. <paramref name="branchNameOverride"/>
    /// and <paramref name="baseBranchOverride"/> follow the server's convention:
    /// null leaves the stored value alone, blank clears it back to the default
    /// (generated per-run naming, and the repository's default branch
    /// respectively). Either way they only affect the item's next run.
    /// </summary>
    Task<bool> UpdateAsync(string workItemId, string title, string description, IEnumerable<string>? tags = null, RemoteAiProviderOverrideMode? aiProviderOverride = null, Guid? aiProviderOverrideId = null, string? branchNameOverride = null, string? baseBranchOverride = null);
    Task<WorkItemView?> GetWorkItemAsync(string workItemId);

    /// <summary>
    /// Server-authoritative listing. Queries the WorkItemServer and merges
    /// engine-only fields from LoopRun rows.
    /// </summary>
    Task<IReadOnlyList<WorkItemView>> ListAsync(
        RemoteWorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take);

    /// <summary>
    /// Lightweight, relationship-aware listing for agent triage. Returns
    /// bodiless <see cref="WorkItemSummary"/> rows carrying dependency ids,
    /// reverse-edge counts, and an actionable flag, filtered/sorted/paged per
    /// <paramref name="query"/>. The whole graph is loaded once to resolve
    /// reverse edges and dependency status, so a single call lets the agent
    /// reconstruct the dependency graph without per-item lookups.
    /// </summary>
    Task<IReadOnlyList<WorkItemSummary>> ListSummariesAsync(WorkItemListQuery query);

    /// <summary>
    /// Aggregate the backlog (optionally scoped to <paramref name="repositoryId"/>)
    /// into status/priority counts and blocked-vs-actionable totals, with no
    /// bodies. Lets an agent orient before drilling into individual items.
    /// </summary>
    Task<BacklogSummary> GetBacklogSummaryAsync(Guid? repositoryId);
    /// <summary>
    /// Send an item that has not started back to Backlog for re-planning.
    /// Refused for an item with a live run behind it — that is
    /// <see cref="CleanupToBacklogAsync"/>'s job.
    /// </summary>
    Task<bool> TransitionToBacklogAsync(string workItemId);
    Task<bool> TransitionToWorkQueueAsync(string workItemId);
    Task<bool> TransitionToReadyAsync(string workItemId);
    Task<bool> TransitionToRunningAsync(string workItemId);
    Task<bool> TransitionToHumanFeedbackAsync(string workItemId, string reason);
    Task<bool> TransitionToDoneAsync(string workItemId);

    /// <summary>
    /// Generic transition entry point. Mirrors the remote server transition contract.
    /// </summary>
    /// <param name="reason">Content stored in the server conversation thread.</param>
    /// <param name="humanFeedbackReason">Short label stored on LoopRun for frontend UI routing. Falls back to <paramref name="reason"/> when null.</param>
    /// <param name="name">Optional author display name for the conversation entry (e.g. the originating node's title).</param>
    Task<bool> TransitionAsync(
        string workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null,
        string? name = null);

    /// <summary>
    /// Append an AI-authored conversation turn (e.g. an AI node's output) to the
    /// work item's thread without changing its status. <paramref name="name"/> is
    /// the author label shown in the UI, typically the node's title.
    /// </summary>
    Task<bool> AppendAiTurnAsync(string workItemId, string name, string content);
    Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<bool> RemoveDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(string workItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId);
    Task<bool> IsReadyAsync(string workItemId);
    /// <summary>
    /// Link a PR to a work item by hand: records it against the item and, when
    /// the item still has a current run, points that run at it too.
    /// </summary>
    Task<bool> LinkPullRequestAsync(string workItemId, string prUrl);

    /// <summary>
    /// Record a PR against the work item on the WorkItem server, which is where
    /// a work item's PRs live: a run's worktree, branch and PR snapshot are
    /// throwaway ILD-local state, but the PR touches the repository and belongs
    /// to the item, so it has to outlive both the run and this ILD instance
    /// (WI-203). Idempotent on the URL — every path that learns something about
    /// a PR (the PR node opening one, a webhook reporting a merge, a human
    /// linking one) reports it here, and reporting the same PR again updates it
    /// in place. Returns false when there is no remote configured or the server
    /// rejected the write; callers treat it as best-effort, since the next read
    /// of the item re-reports anything its runs still carry.
    /// </summary>
    Task<bool> RecordPullRequestAsync(string workItemId, string prUrl, Guid? loopRunId, bool merged = false, DateTime? createdAt = null);
    Task<bool> CleanupToDoneAsync(string workItemId);
    Task<bool> CleanupToBacklogAsync(string workItemId);

    /// <summary>
    /// Commit any uncommitted changes in the work item's current run worktree
    /// and push its branch to origin, using the same built-in repository
    /// functionality the PR node uses. Lets a human keep work produced by a
    /// loop that has no PR node. Returns the pushed branch name on success, or
    /// an error message describing why the push could not happen.
    /// </summary>
    Task<(bool Success, string? Branch, string? Error)> CommitAndPushBranchAsync(string workItemId);

    /// <summary>
    /// The inverse of <see cref="CommitAndPushBranchAsync"/>: pick up commits that
    /// landed on the run branch's own remote counterpart after the run started — a
    /// human fix, a review commit, another ILD instance — by fetching origin and
    /// rebasing the worktree's branch onto <c>origin/&lt;branch&gt;</c>.
    ///
    /// <para>
    /// The same fetch refreshes the run's base branch, and the result reports how
    /// the run branch stands against <c>origin/&lt;base&gt;</c> so the caller can
    /// decide whether a merge is needed. Deciding is all it does: nothing is merged
    /// or rebased onto the base here — syncing onto it is the Start node's job
    /// (ADR-0006).
    /// </para>
    ///
    /// <para>
    /// Runs with the orchestrator's repository credentials, which is the whole point:
    /// under ADR-0014 the agent uid can reach neither the token nor the askpass
    /// helper, so an agent that needs the latest remote commits asks for this rather
    /// than running <c>git pull</c> itself.
    /// </para>
    /// </summary>
    Task<PullBranchResult> PullBranchAsync(string workItemId, CancellationToken cancellationToken = default);
    Task<bool> SubmitHumanFeedbackInputAsync(string workItemId, string input);
    Task<bool> SubmitHumanFeedbackRespondAsync(string workItemId, string input);

    /// <summary>
    /// Route the parked node to its named custom edge <paramref name="edgeName"/>
    /// (a Human node button), passing <paramref name="input"/> as the node's
    /// output for downstream <c>{{PreviousNode.Output}}</c>.
    /// </summary>
    Task<bool> SubmitHumanFeedbackEdgeAsync(string workItemId, string edgeName, string input);
    Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null);

    /// <summary>
    /// Merge the pull request linked to the work item's current run on the
    /// remote provider and, when <paramref name="deleteBranch"/> is set, delete
    /// the source branch afterwards (best effort). On a successful merge the
    /// loop is advanced along the <c>OnSuccess</c> edge — the same continuation
    /// the Approve action uses. A failed merge leaves the work item parked and
    /// does not advance the loop. Returns <c>null</c> when the work item or its
    /// current run cannot be found.
    /// </summary>
    Task<MergePullRequestResult?> MergePullRequestAsync(string workItemId, bool deleteBranch);
    Task<bool> DeleteAsync(string workItemId);
}

/// <summary>
/// Outcome of a <see cref="IWorkItemManager.MergePullRequestAsync"/> call.
/// <paramref name="Merged"/> reports whether the remote merge succeeded;
/// <paramref name="Error"/> carries the reason when it did not. When branch
/// deletion was requested, <paramref name="BranchDeleted"/> says whether it
/// succeeded and <paramref name="BranchWarning"/> describes a best-effort
/// delete failure that did not block the merge.
/// </summary>
public sealed record MergePullRequestResult(
    bool Merged,
    string? Error,
    bool BranchDeleted,
    string? BranchWarning);

/// <summary>
/// What <see cref="IWorkItemManager.PullBranchAsync"/> did, or why it could not.
/// An outcome rather than a bool because the caller — most often an AI node — has
/// a different next move for each: commit first, resolve the listed conflicts,
/// push before pulling, or simply carry on.
/// </summary>
public enum PullBranchOutcome
{
    /// <summary>The branch was rebased onto its remote counterpart.</summary>
    Updated,

    /// <summary>The remote counterpart held nothing the branch did not already have.</summary>
    AlreadyUpToDate,

    /// <summary>The branch has never been pushed, so there is nothing to pull. A no-op, not an error.</summary>
    NoRemoteBranch,

    /// <summary>Uncommitted changes would be rewritten by the rebase. Commit (or discard) them first.</summary>
    DirtyWorktree,

    /// <summary>The rebase hit merge conflicts; it was aborted, so the branch is exactly as it was.</summary>
    Conflict,

    /// <summary>
    /// Git declined the rebase outright — untracked files in the way, a hook
    /// refusing it, an unusable upstream — so nothing was applied and there are no
    /// conflicted files to resolve. Distinct from <see cref="Conflict"/> because a
    /// caller told "conflict" with an empty file list goes looking for conflict
    /// markers that do not exist; the reason is in the message.
    /// </summary>
    RebaseRefused,

    /// <summary>Everything else: no worktree, no repository, unresolvable branch, failed fetch.</summary>
    Failed,
}

/// <summary>
/// Outcome of a <see cref="IWorkItemManager.PullBranchAsync"/> call.
/// <paramref name="Message"/> is human- and agent-readable and always set.
/// <paramref name="Files"/> holds the paths standing in the way — the conflicted
/// ones for <see cref="PullBranchOutcome.Conflict"/>, the uncommitted ones for
/// <see cref="PullBranchOutcome.DirtyWorktree"/> — and is empty otherwise.
///
/// <para>
/// <paramref name="BaseBranch"/>, <paramref name="BehindBase"/> and
/// <paramref name="AheadOfBase"/> answer the separate question the pull leaves
/// open: whether the run branch needs the base branch merged in. They sit
/// alongside <paramref name="Outcome"/> rather than inside it because the two
/// axes are independent — a pull can be up to date with its own remote and still
/// be ten commits behind the base. All three are null when the comparison was
/// not made (the pull never reached the fetch, or no run pins a base); the
/// counts alone are null when the question has no answer (the run branch IS the
/// base, or the base has no remote counterpart).
/// </para>
/// </summary>
public sealed record PullBranchResult(
    PullBranchOutcome Outcome,
    string? Branch,
    string Message,
    IReadOnlyList<string> Files,
    string? BaseBranch = null,
    int? BehindBase = null,
    int? AheadOfBase = null)
{
    /// <summary>
    /// True when the branch is in sync with its remote counterpart afterwards.
    /// <see cref="PullBranchOutcome.NoRemoteBranch"/> counts: there is nothing to
    /// be out of sync with until the branch is pushed.
    /// </summary>
    public bool Success => Outcome
        is PullBranchOutcome.Updated
        or PullBranchOutcome.AlreadyUpToDate
        or PullBranchOutcome.NoRemoteBranch;
}
