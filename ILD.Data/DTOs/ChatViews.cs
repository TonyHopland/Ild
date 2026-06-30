namespace ILD.Data.DTOs;

/// <summary>Renderable transcript turn returned to the chat bubble.</summary>
public sealed record ChatMessageView(
    Guid Id,
    string Role,
    string Content,
    bool Interrupted,
    int Sequence,
    DateTime CreatedAt);

/// <summary>A resumed chat session plus its rehydrated transcript.</summary>
public sealed record ChatSessionView(
    Guid Id,
    string? Name,
    Guid AiProviderId,
    string ProviderType,
    IReadOnlyList<string> Tools,
    DateTime CreatedAt,
    IReadOnlyList<ChatMessageView> Messages);

/// <summary>
/// A lightweight history-list row (ADR-0013): no transcript, just what the chat
/// bubble needs to render a resumable past chat (name + date-stamp).
/// </summary>
public sealed record ChatSessionSummaryView(
    Guid Id,
    string? Name,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
