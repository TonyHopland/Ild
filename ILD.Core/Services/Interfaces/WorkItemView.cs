using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Merged view of a WorkItem combining server-authoritative fields with
/// engine-only execution state. Replaces the local WorkItem entity.
/// </summary>
public sealed class WorkItemView
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public RemoteWorkItemPriority Priority { get; set; }
    public RemoteWorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<RemoteConversationMessage> Conversation { get; set; } = Array.Empty<RemoteConversationMessage>();
    public string? HumanFeedbackActions { get; set; }

    /// <summary>
    /// How this work item overrides the AI provider its AI nodes run against,
    /// and which provider the override targets. Read by the AI node executor.
    /// </summary>
    public RemoteAiProviderOverrideMode AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }

    // Engine-only fields (from LoopRun)
    public Guid? RepositoryId { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? WorktreePath { get; set; }
    public string? BranchName { get; set; }
    public string? PrUrl { get; set; }
    public bool IsPrMerged { get; set; }
    public string? HumanFeedbackReason { get; set; }
    public Guid? CurrentLoopRunId { get; set; }

    /// <summary>
    /// Label of the node the current run is executing (resolved from the run's
    /// CurrentNodeId). Null when there is no active run or the run has not
    /// entered a node yet. Lets the taskboard show the step a running item is on
    /// without opening it.
    /// </summary>
    public string? CurrentNodeLabel { get; set; }
    public bool IsPreviewRunning { get; set; }

    /// <summary>
    /// Badge-relevant PR status, projected from the current run's persisted PR
    /// snapshot. Lets the taskboard card surface the same CI/review/merge tags
    /// the detail dialog's PR view shows while the item is parked awaiting human
    /// feedback. Null when the current run has no PR snapshot yet.
    /// </summary>
    public WorkItemPrStatus? PrStatus { get; set; }

    /// <summary>
    /// Every pull request ever opened against this work item, newest first and
    /// deduplicated by URL. Unlike <see cref="PrUrl"/> — which only ever shows
    /// the <i>current</i> run's PR and therefore empties out the moment the run
    /// goes terminal — these belong to the work item and are held by the
    /// WorkItem server, so they survive the run completing, the item moving to
    /// Done, the retention sweeper deleting the run row, and this ILD instance
    /// being reset (WI-203).
    ///
    /// Added alongside <see cref="PrUrl"/> rather than replacing it: /api/v1 is
    /// add-only (ADR-0002), so <c>prUrl</c> keeps its "current run's PR"
    /// meaning for existing clients.
    /// </summary>
    public IReadOnlyList<WorkItemPullRequest> PullRequests { get; set; } = Array.Empty<WorkItemPullRequest>();
}

/// <summary>
/// One pull request in a work item's history. ADR-0008 gives each run at most
/// one PR, so an item accumulates one entry per run that opened one — but the
/// entry must outlive the run it was observed on, so it carries everything a
/// client needs to render the link without one.
/// </summary>
/// <param name="Url">The PR's URL — the identity a history entry is deduplicated on.</param>
/// <param name="RunId">
/// The most recent run that reported this PR and still exists on this ILD
/// instance — the run that opened it, unless a later run pointed back at the
/// same PR. Null when no such run is left to link to, which is the normal state
/// for an entry whose runs have been reclaimed.
/// </param>
/// <param name="Merged">Whether the PR was observed merged.</param>
/// <param name="Status">
/// Last known badge-relevant status, projected from the run's PR snapshot.
/// That snapshot is throwaway ILD-local state, so this is null once the run
/// that held it is gone — unlike the link itself.
/// </param>
/// <param name="CreatedAt">When the PR was first recorded against this work item.</param>
public sealed record WorkItemPullRequest(
    string Url,
    Guid? RunId,
    bool Merged,
    WorkItemPrStatus? Status,
    DateTime? CreatedAt);

/// <summary>
/// Badge-relevant subset of a <see cref="RemotePrSnapshot"/>, projected onto a
/// work item so the taskboard card can show a PR's CI verdict, review decision,
/// and mergeability without carrying the full snapshot (body, conversation).
/// </summary>
public sealed record WorkItemPrStatus(
    string State,
    bool Merged,
    bool? Mergeable,
    string? MergeableState,
    RemotePrCiStatus Ci,
    bool Approved,
    bool ChangesRequested);
