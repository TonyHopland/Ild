namespace ILD.Tests;

/// <summary>
/// Every test class that drives a <see cref="ILD.Core.Services.Implementations.ManagedAgentService"/>.
/// Its per-agent lock is static, shared by every instance and every test, and the idle cleanup
/// deliberately skips an agent whose lock is taken, so classes working on the same agent key in
/// parallel would make each other's cleanups skip.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManagedAgentInstallsCollection
{
    public const string Name = "ManagedAgentInstalls";
}

/// <summary>Tests observe whether an agent is busy through the idle check itself.</summary>
internal static class ManagedAgentUsageExtensions
{
    public static bool IsBusy(this ILD.Core.Services.Interfaces.IManagedAgentUsage usage, string agentKey)
        => !usage.TryRunWhenIdle(agentKey, static () => { });
}
