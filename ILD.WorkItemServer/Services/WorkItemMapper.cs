using System.Text.Json;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;

namespace ILD.WorkItemServer.Services;

/// <summary>
/// Pure-function helpers that move WorkItem state between persisted JSON
/// strings and typed DTO shapes. Centralised so controllers and the service
/// layer never read/write the JSON columns directly.
/// </summary>
internal static class WorkItemMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<string> ReadTags(WorkItem w)
        => JsonSerializer.Deserialize<List<string>>(w.TagsJson, JsonOpts) ?? new();

    public static IReadOnlyList<Guid> ReadDependencies(WorkItem w)
        => JsonSerializer.Deserialize<List<Guid>>(w.DependenciesJson, JsonOpts) ?? new();

    public static List<ConversationMessage> ReadConversation(WorkItem w)
        => JsonSerializer.Deserialize<List<ConversationMessage>>(w.ConversationJson, JsonOpts) ?? new();

    public static void WriteTags(WorkItem w, IReadOnlyList<string> tags)
        => w.TagsJson = JsonSerializer.Serialize(tags, JsonOpts);

    public static void WriteDependencies(WorkItem w, IReadOnlyList<Guid> deps)
        => w.DependenciesJson = JsonSerializer.Serialize(deps, JsonOpts);

    public static void WriteConversation(WorkItem w, IReadOnlyList<ConversationMessage> messages)
        => w.ConversationJson = JsonSerializer.Serialize(messages, JsonOpts);

    public static WorkItemDto ToDto(WorkItem w) => new()
    {
        Id = w.Id,
        Title = w.Title,
        Description = w.Description,
        CreatedBy = w.CreatedBy,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
        Priority = w.Priority,
        Status = w.Status,
        Tags = ReadTags(w),
        Dependencies = ReadDependencies(w),
        Conversation = ReadConversation(w),
        HumanFeedbackActions = w.HumanFeedbackActions,
        CreatedByLoopRunId = w.CreatedByLoopRunId,
        RepositoryId = w.RepositoryId,
    };
}
