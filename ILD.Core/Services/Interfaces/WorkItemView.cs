using ILD.Core.Services.Remote;

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

    // Engine-only fields (from LoopRun)
    public Guid? RepositoryId { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
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
}
