using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;

namespace ILD.Api.Controllers;

/// <summary>
/// How both preview start surfaces — the UI's <see cref="WorkItemsController"/> and
/// the agent's <see cref="AgentController"/> (MCP <c>start_preview</c>) — build a
/// start: the request's own settings plus what the run's repository contributes,
/// its custom <c>.env</c> and its package feeds' credentials.
/// </summary>
internal static class PreviewStart
{
    public static async Task<WorktreePreviewStartOptions> OptionsAsync(
        WorktreePreviewStartRequest? request, WorkItemView workItem, IProviderStore providers, IPackageFeedResolver feeds)
        => new(
            request?.ProfileName,
            request?.SkipInstall == true,
            request?.PublicHost,
            request?.PortOverrides,
            await providers.GetRepositoryPreviewEnvAsync(workItem.RunRepositoryId),
            workItem.Id,
            (await feeds.ResolveAsync(workItem.RunRepositoryId)).Feeds);
}
