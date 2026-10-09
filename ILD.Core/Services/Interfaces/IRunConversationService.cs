namespace ILD.Core.Services.Interfaces;

/// <summary>
/// One message of a run's conversation, projected from one event.
/// <paramref name="Id"/> is the event's Id, which is also the message's place
/// in the conversation. <paramref name="Role"/> is <c>ai</c>, <c>human</c> or
/// <c>system</c>.
/// </summary>
public sealed record RunConversationMessage(
    long Id, Guid RunId, Guid? RunNodeId, string Role, string Name, string Text, DateTime Timestamp)
{
    public const string Ai = "ai";
    public const string Human = "human";
    public const string System = "system";
}

/// <summary>
/// A run's conversation, read from its event log: the AI turns, what people
/// said to it, and why it started, stopped and waited. The prompt's
/// <c>{{Conversation.*}}</c> variables and the API both read it from here, so
/// the two cannot disagree.
/// </summary>
public interface IRunConversationService
{
    /// <summary>The run's messages in the order they were written; null when there is no such run.</summary>
    Task<IReadOnlyList<RunConversationMessage>?> GetMessagesAsync(Guid runId);
}
