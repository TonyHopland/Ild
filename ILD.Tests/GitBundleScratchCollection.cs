using Xunit;

namespace ILD.Tests;

/// <summary>
/// Tests that read or write the orchestrator's private <c>git-bundles</c> directory,
/// which every <c>RepositoryManager</c> in the test process shares. The isolated-mode
/// tests create bundles there and delete them again, while <see cref="AuditingRunner"/>
/// lists the directory after every orchestrator call and the mirror tests compare it
/// before and after; run side by side, a listed bundle can vanish before it is read.
/// They run one at a time among themselves, in parallel with the rest of the suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GitBundleScratchCollection
{
    public const string Name = "GitBundleScratch";
}
