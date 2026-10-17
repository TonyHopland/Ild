namespace ILD.Core.Services.Interfaces;

public enum LoopTemplateSaveOutcome
{
    Saved,
    TemplateNotFound,

    /// <summary>Another version was saved first; nothing was written.</summary>
    Conflict,

    /// <summary>The proposal of its own Save was no longer pending; nothing was written.</summary>
    ProposalNotPending,
}

/// <summary>A save of a loop template's next version, and what it did to the template's Loop Template Proposals.</summary>
/// <param name="VersionNumber">The version created; null unless saved.</param>
/// <param name="ProposalApplied">Whether the proposal the save came from was marked Applied.</param>
/// <param name="ProposalsChanged">How many proposals the save changed: applied, or made stale.</param>
/// <param name="ChangedChatSessionIds">The chats whose proposals it changed.</param>
public sealed record LoopTemplateSaveResult(
    LoopTemplateSaveOutcome Outcome,
    int? VersionNumber,
    bool ProposalApplied,
    int ProposalsChanged,
    IReadOnlyList<Guid> ChangedChatSessionIds);
