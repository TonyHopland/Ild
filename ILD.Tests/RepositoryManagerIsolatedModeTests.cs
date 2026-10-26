namespace ILD.Tests;

/// <summary>
/// Every <see cref="RepositoryManagerTests"/> test again, with the manager configured
/// for a uid-isolated deployment (ADR-0014): each agent-side git call must be wrapped
/// in <c>setpriv --reuid</c>, and the repository operations must behave exactly as
/// they do without isolation.
/// </summary>
[Collection("Git")]
public sealed class RepositoryManagerIsolatedModeTests : RepositoryManagerTests
{
    protected override bool Isolated => true;
}
