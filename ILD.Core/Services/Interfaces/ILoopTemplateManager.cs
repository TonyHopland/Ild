using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface ILoopTemplateManager
{
    Task<Guid> CreateLoopTemplateAsync(string name, string description, LoopTemplateGraph graph, RecoveryPolicy recoveryPolicy = RecoveryPolicy.AutoResume);
    Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId);
    Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId);
    Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync(int skip = 0, int take = 100, bool includeArchived = false);
    Task ArchiveLoopTemplateAsync(Guid templateId);
    Task UnarchiveLoopTemplateAsync(Guid templateId);

    /// <summary>
    /// A Loop Editor save: the next version of the template, and the name,
    /// description and policy with it. Naming the Loop Template Proposal it was
    /// reviewed from marks that proposal Applied when it can still apply; any
    /// other pending proposal of the template goes Stale. An invalid graph throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<LoopTemplateSaveResult> SaveNewVersionAsync(
        Guid templateId, string name, string description, LoopTemplateGraph graph, RecoveryPolicy recoveryPolicy, Guid? fromProposalId = null);

    /// <summary>
    /// A proposal's own Save: its document as the next version, only while the
    /// proposal is pending and its base version is the latest. A template that
    /// has moved on makes it Stale and writes nothing.
    /// </summary>
    Task<LoopTemplateSaveResult> SaveProposalAsync(LoopTemplateProposal proposal);

    /// <summary>
    /// Check a proposed <c>ild-loop-template/v2</c> document the way a Loop Editor
    /// save is checked, upgrading a v1 document first; a valid one comes back as
    /// the document a save of it would store.
    /// </summary>
    ProposedLoopDocument CheckProposedDocument(string document);
    Task<Guid> CloneLoopTemplateAsync(Guid sourceTemplateId, string newName);
    Task<LoopTemplateVersion> GetVersionAsync(Guid templateId, int version);
    Task<IEnumerable<LoopTemplateVersion>> GetVersionsAsync(Guid templateId);
    Task<LoopTemplateGraph?> GetLatestGraphAsync(Guid templateId);
    Task<LoopTemplateGraph?> GetVersionGraphAsync(Guid templateId, int versionNumber);
    Task<(bool Valid, IReadOnlyList<string> Errors)> ValidateGraphAsync(LoopTemplateGraph graph);

    /// <summary>Deletes the template, making its pending proposals stale.</summary>
    Task<LoopTemplateDeletion> DeleteLoopTemplateAsync(Guid templateId);
}
