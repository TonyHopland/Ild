namespace ILD.Data;

/// <summary>The pending proposals a write made stale, and the chats that made them.</summary>
public sealed record StaledProposals(int Count, IReadOnlyList<Guid> ChatSessionIds)
{
    public static readonly StaledProposals None = new(0, []);
}
