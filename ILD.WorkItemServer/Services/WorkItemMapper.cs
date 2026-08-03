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

    public static IReadOnlyList<string> ReadDependencies(WorkItem w)
        => JsonSerializer.Deserialize<List<string>>(w.DependenciesJson, JsonOpts) ?? new();

    public static List<ConversationMessage> ReadConversation(WorkItem w)
        => JsonSerializer.Deserialize<List<ConversationMessage>>(w.ConversationJson, JsonOpts) ?? new();

    /// <summary>
    /// The item's PRs, sorted newest first on the way out — the order clients
    /// render, and the reason nothing else has to sort them. The stored order is
    /// whatever order they were reported in and is not meaningful; sorting here
    /// rather than on write keeps a list assembled by several reporters
    /// consistently ordered.
    ///
    /// An item with nothing stored — one predating this column — reads as empty,
    /// and so does one whose value cannot be parsed: an unreadable blob must not
    /// take the whole work item down with it, since every read of the item goes
    /// through here, and a reporter with a live run puts its PRs back on the
    /// next pass anyway.
    /// </summary>
    public static List<WorkItemPullRequest> ReadPullRequests(WorkItem w)
    {
        List<WorkItemPullRequest>? prs = null;
        if (!string.IsNullOrEmpty(w.PullRequestsJson))
        {
            try
            {
                prs = JsonSerializer.Deserialize<List<WorkItemPullRequest>>(w.PullRequestsJson, JsonOpts);
            }
            catch (JsonException)
            {
                prs = null;
            }
        }
        prs ??= new();
        prs.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return prs;
    }

    public static void WriteTags(WorkItem w, IReadOnlyList<string> tags)
        => w.TagsJson = JsonSerializer.Serialize(tags, JsonOpts);

    public static void WriteDependencies(WorkItem w, IReadOnlyList<string> deps)
        => w.DependenciesJson = JsonSerializer.Serialize(deps, JsonOpts);

    public static void WriteConversation(WorkItem w, IReadOnlyList<ConversationMessage> messages)
        => w.ConversationJson = JsonSerializer.Serialize(messages, JsonOpts);

    public static void WritePullRequests(WorkItem w, IReadOnlyList<WorkItemPullRequest> prs)
        => w.PullRequestsJson = SerializePullRequests(prs);

    /// <summary>
    /// The serialized column value, for the compare-and-swap write in
    /// <c>RecordPullRequestAsync</c> — which sets the column directly rather
    /// than through a tracked entity.
    /// </summary>
    public static string SerializePullRequests(IReadOnlyList<WorkItemPullRequest> prs)
        => JsonSerializer.Serialize(prs, JsonOpts);

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
        PullRequests = ReadPullRequests(w),
        HumanFeedbackActions = w.HumanFeedbackActions,
        CreatedByLoopRunId = w.CreatedByLoopRunId,
        CreatedByChatSessionId = w.CreatedByChatSessionId,
        RepositoryId = w.RepositoryId,
        AiProviderOverride = w.AiProviderOverride,
        AiProviderOverrideId = w.AiProviderOverrideId,
        BranchNameOverride = w.BranchNameOverride,
    };

    /// <summary>
    /// A custom branch name as it is stored: trimmed, with blank collapsed to
    /// null so "no override" has exactly one representation and readers never
    /// have to ask whether an empty string means "use the empty branch name".
    /// </summary>
    public static string? NormalizeBranchNameOverride(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
