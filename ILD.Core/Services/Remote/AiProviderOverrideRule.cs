namespace ILD.Core.Services.Remote;

/// <summary>
/// The single rule for whether a work item's AI provider override replaces the
/// provider an AI node would otherwise run against. Applied only through
/// <see cref="AiNodeProviderResolver"/>, which the node executor and the remote
/// coordinator share.
/// </summary>
public static class AiProviderOverrideRule
{
    /// <summary>
    /// OverrideAll swaps every AI node; OverrideDefault swaps only nodes that
    /// fell back to the default provider (a node whose tag matched a provider
    /// is left alone). Either way the override is a no-op without a target
    /// provider.
    /// </summary>
    public static bool Applies(RemoteAiProviderOverrideMode mode, Guid? overrideId, bool nodePinsProvider)
        => overrideId is not null && mode switch
        {
            RemoteAiProviderOverrideMode.OverrideAll => true,
            RemoteAiProviderOverrideMode.OverrideDefault => !nodePinsProvider,
            _ => false,
        };
}
