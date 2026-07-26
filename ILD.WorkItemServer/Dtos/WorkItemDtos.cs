using ILD.WorkItemServer.Domain;

namespace ILD.WorkItemServer.Dtos;

public sealed class WorkItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public WorkItemPriority Priority { get; set; }
    public WorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ConversationMessage> Conversation { get; set; } = Array.Empty<ConversationMessage>();

    /// <summary>Every PR opened against this item, newest first.</summary>
    public IReadOnlyList<WorkItemPullRequest> PullRequests { get; set; } = Array.Empty<WorkItemPullRequest>();
    public string? HumanFeedbackActions { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }
    public AiProviderOverrideMode AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }
}

public sealed class CreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;
    public IReadOnlyList<string>? Tags { get; set; }
    public IReadOnlyList<string>? Dependencies { get; set; }
    public WorkItemStatus? ForceStatus { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }
}

public sealed class UpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>
    /// When supplied, replaces the work item's AI provider override. The mode
    /// and target id travel as a unit — supplying the mode also authoritatively
    /// sets <see cref="AiProviderOverrideId"/> (null clears the target).
    /// </summary>
    public AiProviderOverrideMode? AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }
}

public sealed class TransitionRequest
{
    public WorkItemStatus TargetStatus { get; set; }
    public string? Reason { get; set; }
    public string? Actions { get; set; }
    /// <summary>Optional author display name for the conversation entry this
    /// transition appends (e.g. the originating node's title).</summary>
    public string? Name { get; set; }
}

public sealed class TransitionResponse
{
    public bool Success { get; set; }
    public WorkItemStatus ActualStatus { get; set; }
    public string? Reason { get; set; }
}

public sealed class FeedbackRequest
{
    public string? Content { get; set; }
}

public sealed class AppendConversationRequest
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// What came of recording a PR against a work item. Distinguished rather than
/// collapsed into a bool because they mean different things to a caller: only
/// <see cref="NotFound"/> says the request will never succeed, while
/// <see cref="Conflict"/> is worth retrying and must not be reported to a
/// client as "no such work item".
/// </summary>
public enum RecordPullRequestOutcome
{
    /// <summary>Recorded, or already known exactly as reported.</summary>
    Recorded = 0,

    /// <summary>The report carried no URL, so there was nothing to record.</summary>
    InvalidRequest = 1,

    /// <summary>No work item with that id.</summary>
    NotFound = 2,

    /// <summary>
    /// Other writers kept winning the race for the item's PR list. Nothing was
    /// recorded and nothing was lost; the same report will go through on a
    /// later attempt.
    /// </summary>
    Conflict = 3,
}

public sealed class RecordPullRequestRequest
{
    public string Url { get; set; } = string.Empty;

    /// <summary>The ILD loop run that opened the PR, when it came from one.</summary>
    public Guid? LoopRunId { get; set; }

    /// <summary>Whether the caller has observed the PR merged. Never un-merges an entry.</summary>
    public bool Merged { get; set; }

    /// <summary>
    /// When the PR entered the item's history — the start of the run that
    /// opened it, so history keeps the runs' order however late the client gets
    /// around to reporting it. Defaults to the server clock.
    /// </summary>
    public DateTime? CreatedAt { get; set; }
}

public sealed class AddDependencyRequest
{
    public string DependencyId { get; set; } = string.Empty;
}

public sealed class PollResponse
{
    public IReadOnlyList<WorkItemDto> ActiveItems { get; set; } = Array.Empty<WorkItemDto>();
    public IReadOnlyList<WorkItemDto> ReadyItems { get; set; } = Array.Empty<WorkItemDto>();
}
