using Xunit;

namespace ILD.Tests;

/// <summary>
/// Tests that mutate the process-global <c>PATH</c>/<c>HOME</c> environment must
/// not run concurrently — they share one mutable process environment and would
/// otherwise clobber each other's transient state. Mirrors
/// <see cref="AuthEnvironmentCollection"/> for the auth env vars.
/// </summary>
[CollectionDefinition("EnvironmentPath", DisableParallelization = true)]
public sealed class EnvironmentPathCollection
{
}
