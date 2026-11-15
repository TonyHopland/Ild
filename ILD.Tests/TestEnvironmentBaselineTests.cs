using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Security;

namespace ILD.Tests;

[Collection(ProcessGlobalStateCollection.Name)]
public class TestEnvironmentBaselineTests
{
    // What TestEnvironmentBaseline deliberately sets; anything else matching is leaked ambient state.
    private static readonly string[] Pinned =
    [
        AgentIsolation.PrivateRootEnvVar,
        AgentIsolation.AgentReadRootEnvVar,
    ];

    [Fact]
    public void NoAmbientIldAgentOrProxyVariableSurvivesIntoTheTestHost()
    {
        var leaked = TestEnvironmentBaseline.AmbientVariableNames().Except(Pinned).ToList();

        Assert.True(leaked.Count == 0, "Ambient variables reached the test host: " + string.Join(", ", leaked));
    }

    [Fact]
    public void AgentReadRootIsAWritablePerRunDirectory()
    {
        var root = AgentIsolation.AgentReadRoot;

        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "ild-tests-private-"), root);
        var probe = Path.Combine(root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "x");
        File.Delete(probe);
    }

    [Fact]
    public void LogLevelAndSecretListAreNotInherited()
    {
        Assert.Null(Environment.GetEnvironmentVariable("ILD_LOG_LEVEL"));
        Assert.Null(Environment.GetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar));
        Assert.Null(Environment.GetEnvironmentVariable(SessionTokenHasher.PepperEnvVar));
    }
}
