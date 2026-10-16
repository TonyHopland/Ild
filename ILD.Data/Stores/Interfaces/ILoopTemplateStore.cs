using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public record TemplateGraph(
    LoopTemplate Template,
    LoopTemplateVersion Version,
    IReadOnlyList<LoopNode> Nodes,
    IReadOnlyList<LoopNodeEdge> Edges);

/// <summary>One node config of the latest version of a template.</summary>
public sealed record LatestNodeConfig(Guid TemplateId, string TemplateName, string? Config);

/// <summary>
/// A new version of an existing template, with the name, description and policy
/// a save writes alongside it. <see cref="Version"/>, <see cref="Nodes"/> and
/// <see cref="Edges"/> are ready to insert except for the version number, which
/// the store assigns; <see cref="Document"/> is the same loop as an
/// <c>ild-loop-template/v2</c> document, to compare with a proposal.
/// </summary>
public sealed record LoopTemplateVersionWrite(
    Guid TemplateId,
    string Name,
    string Description,
    RecoveryPolicy? RecoveryPolicy,
    LoopTemplateVersion Version,
    IReadOnlyList<LoopNode> Nodes,
    IReadOnlyList<LoopNodeEdge> Edges,
    string Document)
{
    /// <summary>
    /// The Loop Template Proposal the save comes from. It is marked Applied only
    /// while it is pending, of this template, and based on the version this save
    /// follows; otherwise the save is a plain save and leaves it alone.
    /// </summary>
    public Guid? FromProposalId { get; init; }

    /// <summary>
    /// The save is the proposal's own Save: nothing is written unless it applies.
    /// When the template has moved on past its base version the proposal goes Stale.
    /// </summary>
    public bool RequireProposal { get; init; }
}

public enum LoopTemplateVersionSaveOutcome
{
    Saved,
    TemplateNotFound,

    /// <summary>Another version was saved first; nothing was written.</summary>
    Conflict,

    /// <summary>A required proposal was no longer pending; nothing was written.</summary>
    ProposalNotPending,
}

/// <param name="VersionNumber">The version created; null unless saved.</param>
/// <param name="ProposalApplied">Whether the named proposal was marked Applied.</param>
/// <param name="ProposalsChanged">How many proposals the save changed: applied, or made stale.</param>
/// <param name="ChangedChatSessionIds">The chats whose proposals it changed.</param>
public sealed record LoopTemplateVersionSave(
    LoopTemplateVersionSaveOutcome Outcome,
    int? VersionNumber,
    bool ProposalApplied,
    int ProposalsChanged,
    IReadOnlyList<Guid> ChangedChatSessionIds);

public interface ILoopTemplateStore
{
    Task<LoopTemplate?> GetByIdAsync(Guid id);
    Task<LoopTemplate?> GetByVersionIdAsync(Guid versionId);
    Task<TemplateGraph?> GetTemplateGraphByVersionIdAsync(Guid versionId);
    Task<IReadOnlyList<LoopTemplate>> GetAllAsync(int skip = 0, int take = 100, bool includeArchived = false);
    Task<LoopTemplateVersion?> GetLatestVersionAsync(Guid templateId);
    Task<LoopTemplateVersion?> GetVersionByIdAsync(Guid versionId);
    Task<LoopTemplateVersion?> GetVersionAsync(Guid templateId, int versionNumber);
    Task<IReadOnlyList<LoopTemplateVersion>> GetVersionsAsync(Guid templateId);
    Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId);

    /// <summary>
    /// Every node config of the latest version of each non-archived template, in
    /// one query, ordered by template name then id.
    /// </summary>
    Task<IReadOnlyList<LatestNodeConfig>> GetLatestNodeConfigsOfLiveTemplatesAsync();
    Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForVersionAsync(Guid versionId);
    Task CreateTemplateAsync(LoopTemplate template);
    Task UpdateTemplateAsync(LoopTemplate template);
    Task SetArchivedAsync(Guid templateId, bool archived);

    /// <summary>
    /// Delete the template and make its pending proposals stale, in one
    /// transaction: with no template left to save to they could never be decided.
    /// </summary>
    Task<StaledLoopTemplateProposals> DeleteTemplateAsync(LoopTemplate template);

    /// <summary>
    /// Save <paramref name="write"/> as the template's next version and make every
    /// pending proposal of the template stale, except the one it applies, in one
    /// transaction. Every save of a new version of an existing template goes
    /// through here.
    /// </summary>
    Task<LoopTemplateVersionSave> AddVersionAsync(LoopTemplateVersionWrite write, CancellationToken ct = default);
    Task CreateVersionAsync(LoopTemplateVersion version);
    Task CreateNodesAsync(IReadOnlyList<LoopNode> nodes);
    Task CreateEdgesAsync(IReadOnlyList<LoopNodeEdge> edges);
    Task DeleteNodesForVersionAsync(Guid versionId);
    Task DeleteEdgesForVersionAsync(Guid versionId);
    Task DeleteVersionsForTemplateAsync(Guid templateId);
    Task SaveChangesAsync();
}
