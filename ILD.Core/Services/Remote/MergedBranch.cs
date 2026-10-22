using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The source branch of a pull request that has just been merged, by a person
/// or by ILD's AutoMerge.
/// </summary>
public static class MergedBranch
{
    /// <summary>
    /// Delete the branch, best effort: the merge already happened, so a branch
    /// left behind holds nothing up. A failure is recorded on the run as
    /// <see cref="EventType.BranchDeleteFailed"/> and returned as the warning to
    /// show; null when the branch is gone.
    /// </summary>
    public static async Task<string?> TryDeleteAsync(
        IRemoteProvider remote, IEventLogService? events, ILogger logger, Guid runId, string repoUrl, string branch)
    {
        bool deleted;
        try
        {
            deleted = await remote.DeleteBranchAsync(repoUrl, branch);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete branch {Branch} of run {RunId}", branch, runId);
            deleted = false;
        }
        if (deleted)
            return null;

        var warning = $"PR merged, but the branch '{branch}' could not be deleted.";
        await events.TryAppendAsync(runId, EventType.BranchDeleteFailed, warning, logger);
        return warning;
    }
}
