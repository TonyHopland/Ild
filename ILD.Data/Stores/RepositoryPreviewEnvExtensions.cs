using ILD.Data.Stores.Interfaces;

namespace ILD.Data.Stores;

/// <summary>
/// Single shared resolver for a work item's repository custom preview <c>.env</c>
/// (see <c>Repository.PreviewEnv</c>), so every preview start path — the human
/// <c>WorkItemsController</c>/<c>AgentController</c> and the agent-tool
/// <c>AIProviderService</c> — injects the same value through one code path instead
/// of each re-deriving it.
/// </summary>
public static class RepositoryPreviewEnvExtensions
{
    public static async Task<string?> GetRepositoryPreviewEnvAsync(this IProviderStore store, Guid? repositoryId)
    {
        if (repositoryId is null) return null;
        var repo = await store.GetRepositoryByIdAsync(repositoryId.Value);
        return repo?.PreviewEnv;
    }
}
