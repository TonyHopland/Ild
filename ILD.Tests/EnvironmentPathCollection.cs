using Xunit;

namespace ILD.Tests;

/// <summary>
/// Tests that mutate the process-global environment — <c>PATH</c>/<c>HOME</c>,
/// and the agent-isolation variables such as the scratch root, which
/// <c>AgentIsolation</c> reads live on every call — must not run concurrently:
/// they share one mutable process environment and would otherwise clobber each
/// other's transient state, or restore a value a neighbour had just set.
/// Mirrors <see cref="AuthEnvironmentCollection"/> for the auth env vars.
/// </summary>
[CollectionDefinition("EnvironmentPath", DisableParallelization = true)]
public sealed class EnvironmentPathCollection
{
}
