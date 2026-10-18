using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="IPreviewDataService"/>
public sealed class PreviewDataService(PreviewDataClaims claims, IConfiguration config, ILogger<PreviewDataService> logger) : IPreviewDataService
{
    public async Task<PreviewDataDeletion> DeleteAsync(Guid repositoryId)
    {
        if (!claims.TryBeginReset(repositoryId, out var refusal))
            return new PreviewDataDeletion(PreviewDataDeletionOutcome.Refused, refusal);

        try
        {
            // Not cancellable: a delete stopped halfway leaves the next preview a half-emptied folder.
            if (await AgentWritableFiles.DeleteAsync([PreviewVolumes.RepositoryFolder(config, repositoryId)]))
                return new PreviewDataDeletion(PreviewDataDeletionOutcome.Deleted);

            logger.LogWarning("Could not fully delete the preview data of repository {RepositoryId}", repositoryId);
            return new PreviewDataDeletion(PreviewDataDeletionOutcome.Failed, "The preview data could not be fully deleted.");
        }
        finally
        {
            claims.EndReset(repositoryId);
        }
    }
}
