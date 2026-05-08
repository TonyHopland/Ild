using ILD.WorkItemServer.Domain;

namespace ILD.WorkItemServer.Dtos;

public sealed class WorkItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public WorkItemPriority Priority { get; set; }
    public WorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<Guid> Dependencies { get; set; } = Array.Empty<Guid>();
    public IReadOnlyList<ConversationMessage> Conversation { get; set; } = Array.Empty<ConversationMessage>();
    public string? HumanFeedbackActions { get; set; }
}

public sealed class CreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;
    public IReadOnlyList<string>? Tags { get; set; }
    public IReadOnlyList<Guid>? Dependencies { get; set; }
    public WorkItemStatus? ForceStatus { get; set; }
}

public sealed class UpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
}

public sealed class TransitionRequest
{
    public WorkItemStatus TargetStatus { get; set; }
    public string? Reason { get; set; }
    public string? Actions { get; set; }
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

public sealed class AddDependencyRequest
{
    public Guid DependencyId { get; set; }
}

public sealed class PollResponse
{
    public IReadOnlyList<WorkItemDto> ActiveItems { get; set; } = Array.Empty<WorkItemDto>();
    public IReadOnlyList<WorkItemDto> ReadyItems { get; set; } = Array.Empty<WorkItemDto>();
}
