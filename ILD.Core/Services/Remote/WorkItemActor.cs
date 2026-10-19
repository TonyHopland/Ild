using System.Text.Json;

namespace ILD.Core.Services.Remote;

/// <summary>Mirrors the WorkItem server's WorkItemActorKind.</summary>
public enum RemoteWorkItemActorKind
{
    Human = 0,
    Agent = 1,
    Chat = 2,
    System = 3,
}

/// <summary>
/// Who a change to a work item is made for, as its history records it. Every
/// mutation the <see cref="IWorkItemServerClient"/> sends takes one, and the
/// WorkItem server refuses a mutation without it.
/// </summary>
/// <param name="Detail">What acted on the user's behalf: a loop run's step, a chat, a system process, an edit proposal.</param>
/// <param name="WorkItemId">The work item the acting loop run belongs to.</param>
public sealed record WorkItemActor(string Username, RemoteWorkItemActorKind Kind, string? Detail = null, string? WorkItemId = null)
{
    public const string HeaderName = "X-ILD-Actor";

    private static readonly JsonSerializerOptions HeaderJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A signed-in person acting through the UI.</summary>
    public static WorkItemActor Human(string username) => new(username, RemoteWorkItemActorKind.Human);

    /// <summary>
    /// A loop run, acting as the instance's user: named by its step when it has
    /// one running, and tied to the work item it is working on.
    /// </summary>
    public static WorkItemActor Agent(string instanceUsername, Guid runId, string? nodeLabel, string runWorkItemId)
        => new(instanceUsername, RemoteWorkItemActorKind.Agent,
            string.IsNullOrWhiteSpace(nodeLabel) ? $"run {runId}" : $"{nodeLabel} (run {runId})",
            runWorkItemId);

    /// <summary>A chat session, acting as the user it belongs to.</summary>
    public static WorkItemActor Chat(string username, Guid chatSessionId)
        => new(username, RemoteWorkItemActorKind.Chat, $"chat {chatSessionId}");

    /// <summary>A process of this instance with no person or agent behind it, acting as the instance's user.</summary>
    public static WorkItemActor System(string instanceUsername, string reason)
        => new(instanceUsername, RemoteWorkItemActorKind.System, $"system: {reason}");

    /// <summary>The X-ILD-Actor header value: base64 of UTF-8 JSON, so any name travels intact.</summary>
    public string ToHeaderValue()
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new HeaderBody(Username, Kind.ToString(), Detail, WorkItemId), HeaderJson));

    private sealed record HeaderBody(string Username, string Kind, string? Detail, string? WorkItemId);
}
