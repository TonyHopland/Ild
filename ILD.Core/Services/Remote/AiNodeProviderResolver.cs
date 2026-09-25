using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The single answer to which provider an AI node runs on. Shared by the node
/// executor (which claims the concurrency slot) and the remote coordinator
/// (which peeks capacity before resuming a parked run) so the two cannot drift
/// — if they disagree, runs either strand forever or flap between resume and
/// re-park.
/// </summary>
public static class AiNodeProviderResolver
{
    /// <summary>
    /// The provider holding the node's tag (compared trimmed and
    /// case-insensitively), else the default provider; then swapped for the
    /// work item's override when <see cref="AiProviderOverrideRule"/> says it
    /// applies. Exactly one of the two results is set.
    /// </summary>
    public static async Task<(AiProvider? Provider, string? Error)> ResolveAsync(
        IProviderStore store, string? tag, RemoteAiProviderOverrideMode overrideMode, Guid? overrideId)
    {
        var trimmedTag = tag?.Trim();
        var provider = string.IsNullOrEmpty(trimmedTag) ? null : await store.GetAiProviderByTagAsync(trimmedTag);
        var tagMatched = provider is not null;
        if (provider is null)
        {
            provider = await store.GetDefaultAiProviderAsync();
            if (provider is null)
            {
                return (null, string.IsNullOrEmpty(trimmedTag)
                    ? "AI node has no provider: no default provider is configured"
                    : $"AI node has no provider: no default provider is configured and no provider has tag '{trimmedTag}'");
            }
        }

        if (AiProviderOverrideRule.Applies(overrideMode, overrideId, nodePinsProvider: tagMatched))
        {
            provider = await store.GetAiProviderByIdAsync(overrideId!.Value);
            if (provider is null)
                return (null, $"Work item AI provider override {overrideId} not found");
        }

        return (provider, null);
    }
}
