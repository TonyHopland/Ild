using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>Tells a work item's open Preview tabs to re-read.</summary>
internal sealed class PreviewNotifier(IWorkItemNotifier? notifier, ILogger logger)
{
    /// <summary>A hint only: a failed push is logged and never fails the start it describes.</summary>
    public async Task PublishAsync(string? workItemId)
    {
        if (notifier == null || workItemId == null)
            return;

        try
        {
            await notifier.PreviewStateChangedAsync(workItemId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish a preview state change for work item {WorkItemId}", workItemId);
        }
    }
}
