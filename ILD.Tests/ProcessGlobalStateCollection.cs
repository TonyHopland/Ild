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
/// <item><see cref="Integration.StorageOverviewTests"/> points the agent scratch
/// root (<c>ILD_AGENT_SCRATCH_ROOT</c>) at a directory of its own, which every
/// scratch directory the process creates would then land in, and in another test
/// clears it.</item>
/// <item><see cref="SessionTokenHasherTests"/> and <see cref="AuthServicePepperTests"/>
/// set the static session-token pepper, which every host and every
/// <see cref="ILD.Core.Services.Implementations.AuthService"/> hashes tokens
/// with.</item>
/// <item><see cref="ProductionKeyDerivationTests"/> switches off the static PBKDF2 iteration
/// overrides that lower every backup and sign-in in the process, to pin the production counts.</item>
/// </list>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection
{
    public const string Name = "ProcessGlobalState";
}
