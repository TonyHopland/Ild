namespace ILD.Data.DTOs;

/// <summary>Renderable transcript turn returned to the chat bubble.</summary>
public sealed record ChatMessageView(
    Guid Id,
    string Role,
    string Content,
    bool Interrupted,
    int Sequence,
    DateTime CreatedAt);

/// <summary>The current user's chat session plus its rehydrated transcript.</summary>
public sealed record ChatSessionView(
    Guid Id,
    Guid AiProviderId,
    string ProviderType,
    IReadOnlyList<string> Tools,
    DateTime CreatedAt,
    IReadOnlyList<ChatMessageView> Messages);
