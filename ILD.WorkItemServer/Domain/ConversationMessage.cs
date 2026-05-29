namespace ILD.WorkItemServer.Domain;

/// <summary>
/// One entry in a work item's AI ↔ Human dialogue. Appended by the server
/// when a transition supplies a reason (HumanFeedback / Done), when a human
/// replies (feedback endpoint), or when an AI node posts its output (the
/// conversation endpoint).
/// </summary>
/// <param name="Role">Coarse author kind used for styling: "ai" or "human".</param>
/// <param name="Content">The message body (markdown).</param>
/// <param name="Timestamp">When the entry was recorded (UTC).</param>
/// <param name="Name">
/// Optional display name for the author — e.g. the node's title ("AI Coder",
/// "AI Reviewer"). Null for legacy entries; the UI falls back to the role.
/// </param>
public sealed record ConversationMessage(
    string Role,
    string Content,
    DateTime Timestamp,
    string? Name = null);
