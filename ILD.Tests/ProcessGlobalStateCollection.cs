using Xunit;

namespace ILD.Tests;

/// <summary>
/// Tests that change something the whole test process shares, so they run one at
/// a time and after every parallel test. Everything else supplies its values to
/// its own instance instead, and stays out of here. The members, and why each one
/// cannot run beside anything:
/// <list type="bullet">
/// <item><see cref="WorktreePreviewServiceEnvironmentIsolationTests"/> seeds the
/// orchestrator's secrets and topology into the real process environment, because
/// what it proves is that a preview child does not inherit them.</item>
/// <item><see cref="ChatRepositoryClonerStartInfoTests"/> seeds git's credential
/// variables and the orchestrator's secrets into the real process environment,
/// because what it proves is that git in a chat's clone does not inherit them.</item>
/// <item><see cref="PiAdapterAgentDirectoryTests"/> swaps pi's shared, fixed-name
/// scratch directories for links, which every pi turn in the process goes
/// through.</item>
/// <item><see cref="Integration.StorageOverviewTests"/> sets <c>ILD_DATA_PATH</c>
/// while its host builds, which any host built beside it would take as its data
/// root too, and points the agent scratch root at a directory of its own, which
/// every scratch directory the process creates would then land in.</item>
/// <item><see cref="SessionTokenHasherTests"/> and <see cref="AuthServicePepperTests"/>
/// set the static session-token pepper, which every host and every
/// <see cref="ILD.Core.Services.Implementations.AuthService"/> hashes tokens
/// with.</item>
/// </list>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection
{
    public const string Name = "ProcessGlobalState";
}
