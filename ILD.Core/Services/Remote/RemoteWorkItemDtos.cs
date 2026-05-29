namespace ILD.Core.Services.Remote;

/// <summary>
/// Mirrors the WorkItem status enum exposed by the WorkItem server's REST
/// surface. Kept as an independent type so ILD.Core does not have to take a
/// project reference on the server assembly.
/// </summary>
public enum RemoteWorkItemStatus
{
    Backlog = 0,
    WorkQueue = 1,
    Ready = 2,
    Running = 3,
    HumanFeedback = 4,
    WaitingForIld = 5,
    Done = 6,
}

public enum RemoteWorkItemPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public sealed record RemoteConversationMessage(string Role, string Content, DateTime Timestamp, string? Name = null);

public sealed class RemoteWorkItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public RemoteWorkItemPriority Priority { get; set; }
    public RemoteWorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();
    public IReadOnlyList<RemoteConversationMessage> Conversation { get; set; } = Array.Empty<RemoteConversationMessage>();
    public string? HumanFeedbackActions { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? RepositoryId { get; set; }
}

public sealed class RemoteCreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public RemoteWorkItemPriority Priority { get; set; } = RemoteWorkItemPriority.Medium;
    public IReadOnlyList<string>? Tags { get; set; }
    public IReadOnlyList<string>? Dependencies { get; set; }
    public RemoteWorkItemStatus? ForceStatus { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? RepositoryId { get; set; }
}

public sealed class RemoteUpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
}

public sealed class RemoteTransitionRequest
{
    public RemoteWorkItemStatus TargetStatus { get; set; }
    public string? Reason { get; set; }
    public string? Actions { get; set; }
    /// <summary>Optional author display name for the conversation entry this
    /// transition appends (e.g. the originating node's title).</summary>
    public string? Name { get; set; }
}

public sealed class RemoteTransitionResponse
{
    public bool Success { get; set; }
    public RemoteWorkItemStatus ActualStatus { get; set; }
    public string? Reason { get; set; }
}

public sealed class RemotePollResponse
{
    public IReadOnlyList<RemoteWorkItem> ActiveItems { get; set; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> ReadyItems { get; set; } = Array.Empty<RemoteWorkItem>();
}
