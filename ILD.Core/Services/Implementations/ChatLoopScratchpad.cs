using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// In-memory <see cref="IChatLoopScratchpad"/> (loop editor context, ADR-0011).
/// A process-wide singleton: the chat turn writes it (per-message overwrite) and
/// the agent-scoped API reads it back through the <c>get_current_loop</c> tool, so
/// both the turn's DI scope and the MCP server's request scope must see the same
/// store. Nothing is persisted — the live loop lives only in the browser, and the
/// scratchpad is just the per-turn relay.
/// </summary>
public sealed class ChatLoopScratchpad : IChatLoopScratchpad
{
    private readonly ConcurrentDictionary<Guid, string> _documents = new();

    public void Set(Guid chatSessionId, string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
            _documents.TryRemove(chatSessionId, out _);
        else
            _documents[chatSessionId] = document;
    }

    public string? Get(Guid chatSessionId)
        => _documents.TryGetValue(chatSessionId, out var document) ? document : null;

    public void Clear(Guid chatSessionId) => _documents.TryRemove(chatSessionId, out _);
}
