namespace ILD.Core.Services.Interfaces;

/// <summary>The Loop Template Proposals a template's deletion made stale, and the chats that made them.</summary>
public sealed record LoopTemplateDeletion(int ProposalsStaled, IReadOnlyList<Guid> ChangedChatSessionIds)
{
    public static readonly LoopTemplateDeletion None = new(0, []);
}
