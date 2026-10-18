namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Removes a repository's persisted preview data (Keep preview data between runs):
/// the repository's reset and the storage page's delete. Only a person reaches
/// this; no agent endpoint or MCP tool does.
/// </summary>
public interface IPreviewDataService
{
    /// <summary>
    /// Deletes the repository's whole preview data folder, or refuses while a preview
    /// of the repository holds it. A preview started while this runs is refused.
    /// </summary>
    Task<PreviewDataDeletion> DeleteAsync(Guid repositoryId);
}

public enum PreviewDataDeletionOutcome
{
    Deleted,
    Refused,
    Failed,
}

/// <param name="Error">Why it was refused or failed; null once deleted.</param>
public sealed record PreviewDataDeletion(PreviewDataDeletionOutcome Outcome, string? Error = null);
