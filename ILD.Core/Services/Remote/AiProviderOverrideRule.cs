namespace ILD.Core.Services.Remote;

/// <summary>
/// The single rule for whether a work item's AI provider override replaces the
/// provider an AI node would otherwise run against. Shared by the node executor
/// (which claims the concurrency slot) and the remote coordinator (which peeks
/// capacity before resuming a parked run) so the two cannot drift — if they
/// disagree, runs either strand forever or flap between resume and re-park.
/// </summary>
public static class AiProviderOverrideRule
{
    /// <summary>
    /// OverrideAll swaps every AI node; OverrideDefault swaps only nodes that
    /// fell back to the default provider (a node deliberately pinned to a
    /// specific provider is left alone). Either way the override is a no-op
    /// without a target provider.
    /// </summary>
    public static bool Applies(RemoteAiProviderOverrideMode mode, Guid? overrideId, bool nodePinsProvider)
        => overrideId is not null && mode switch
        {
            RemoteAiProviderOverrideMode.OverrideAll => true,
            RemoteAiProviderOverrideMode.OverrideDefault => !nodePinsProvider,
            _ => false,
        };
}
