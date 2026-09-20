namespace ILD.Data.DTOs;

/// <summary>Renderable transcript turn returned to the chat bubble.</summary>
public sealed record ChatMessageView(
    Guid Id,
    string Role,
    string Content,
    bool Interrupted,
    int Sequence,
    DateTime CreatedAt);

/// <summary>
/// A resumed chat session plus its rehydrated transcript.
/// <paramref name="ActiveTurnId"/> is the turn the server has in flight for this
/// chat, or null when it is idle — turn liveness belongs to the turn runner
/// rather than to the stored session, so it is filled in by the controller.
/// </summary>
public sealed record ChatSessionView(
    Guid Id,
    string? Name,
    Guid AiProviderId,
    string ProviderType,
    IReadOnlyList<string> Tools,
    DateTime CreatedAt,
    IReadOnlyList<ChatMessageView> Messages,
    Guid? ActiveTurnId = null);

/// <summary>
/// A lightweight history-list row (ADR-0013): no transcript, just what the chat
/// bubble needs to render a resumable past chat (name + date-stamp).
/// </summary>
public sealed record ChatSessionSummaryView(
    Guid Id,
    string? Name,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
