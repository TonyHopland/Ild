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
    /// The work item's override when <see cref="AiProviderOverrideRule"/> says
    /// it applies (a node whose tag no provider holds counts as falling back to
    /// the default, whether or not a default exists); otherwise the provider
    /// holding the node's tag (compared trimmed and case-insensitively), else
    /// the default provider. Exactly one of the two results is set.
    /// </summary>
    public static async Task<(AiProvider? Provider, string? Error)> ResolveAsync(
        IProviderStore store, string? tag, RemoteAiProviderOverrideMode overrideMode, Guid? overrideId)
    {
        var trimmedTag = tag?.Trim();
        var tagged = string.IsNullOrEmpty(trimmedTag) ? null : await store.GetAiProviderByTagAsync(trimmedTag);

        if (AiProviderOverrideRule.Applies(overrideMode, overrideId, nodePinsProvider: tagged is not null))
        {
            var overrideProvider = await store.GetAiProviderByIdAsync(overrideId!.Value);
            return overrideProvider is null
                ? (null, $"Work item AI provider override {overrideId} not found")
                : (overrideProvider, null);
        }

        var provider = tagged ?? await store.GetDefaultAiProviderAsync();
        if (provider is null)
        {
            return (null, string.IsNullOrEmpty(trimmedTag)
                ? "AI node has no provider: no default provider is configured"
                : $"AI node has no provider: no default provider is configured and no provider has tag '{trimmedTag}'");
        }

        return (provider, null);
    }
}
