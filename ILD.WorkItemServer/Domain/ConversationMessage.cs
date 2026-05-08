namespace ILD.WorkItemServer.Domain;

/// <summary>
/// One entry in a work item's AI ↔ Human dialogue. Appended by the server
/// on transitions to response states (HumanFeedback, WaitingForIld, Done)
/// when a reason is supplied.
/// </summary>
public sealed record ConversationMessage(
    string Role,
    string Content,
    DateTime Timestamp);
