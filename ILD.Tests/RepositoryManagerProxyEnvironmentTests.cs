using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

[Collection("Git")]
public class RepositoryManagerProxyEnvironmentTests : IDisposable
{
    private static readonly Dictionary<string, string> Noise = new()
    {
        ["NODE_USE_ENV_PROXY"] = "1",
        ["GIT_DIR"] = "/elsewhere/.git",
        ["ILD_SECRET_KEY"] = "s3cret",
        ["FOO_UNRELATED"] = "x",
    };

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(IReadOnlyDictionary<string, string?>? Environment, ProcessRunOptions? Options)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
        {
            Calls.Add((environmentVariables == null ? null : new Dictionary<string, string?>(environmentVariables), options));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-tests-" + Guid.NewGuid().ToString("N"));

    public RepositoryManagerProxyEnvironmentTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static Func<string, string?> Reader(IReadOnlyDictionary<string, string> values)
        => name => values.TryGetValue(name, out var v) ? v : null;

    private static Dictionary<string, string> AllProxies()
        => RepositoryManager.ProxyEnvironmentKeys.ToDictionary(n => n, n => $"http://user:pw@proxy.invalid:3128/{n}");

    private async Task<IReadOnlyDictionary<string, string?>> LsRemoteEnvironment(
        IReadOnlyDictionary<string, string> orchestratorEnvironment, GitAuthOptions? auth = null)
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, null, Path.Combine(_tmp, "wt"), null, Path.Combine(_tmp, "mirrors"),
            readEnvironment: Reader(orchestratorEnvironment));

        await mgr.ProbeRemoteAsync("https://example.invalid/r.git", null, TestContext.Current.CancellationToken, auth);

        var call = Assert.Single(runner.Calls, c => c.Options?.CleanEnvironment == true);
        Assert.NotNull(call.Environment);
        return call.Environment!;
    }

    private static readonly GitAuthOptions Credential = new("https://example.invalid/r.git", "token-123");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ls_remote_passes_every_set_proxy_variable_unchanged_and_nothing_else(bool withCredential)
    {
        var auth = withCredential ? Credential : null;
        var baseline = await LsRemoteEnvironment(new Dictionary<string, string>(), auth);

        var proxies = AllProxies();
        var env = await LsRemoteEnvironment(proxies.Concat(Noise).ToDictionary(p => p.Key, p => p.Value), auth);

        foreach (var (name, value) in proxies)
            Assert.Equal(value, env[name]);
        Assert.Equal(baseline.Keys.Concat(RepositoryManager.ProxyEnvironmentKeys).Order(StringComparer.Ordinal), env.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in baseline)
            Assert.Equal(value, env[key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ls_remote_environment_is_unchanged_when_no_proxy_variable_is_set(bool withCredential)
    {
        var auth = withCredential ? Credential : null;
        var env = await LsRemoteEnvironment(Noise, auth);

        Assert.DoesNotContain(env.Keys, k => RepositoryManager.ProxyEnvironmentKeys.Contains(k));
        foreach (var name in Noise.Keys) Assert.DoesNotContain(name, env.Keys);
    }

    [Fact]
    public async Task Ls_remote_passes_only_the_proxy_variables_that_are_set()
    {
        var env = await LsRemoteEnvironment(new Dictionary<string, string>
        {
            ["https_proxy"] = "http://proxy.invalid:3128",
            ["NO_PROXY"] = "localhost,.example.test",
        });

        Assert.Equal("http://proxy.invalid:3128", env["https_proxy"]);
        Assert.Equal("localhost,.example.test", env["NO_PROXY"]);
        Assert.Equal(["NO_PROXY", "https_proxy"], env.Keys.Where(k => RepositoryManager.ProxyEnvironmentKeys.Contains(k)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Mirror_clone_gives_every_orchestrator_git_call_the_proxy_variables_and_nothing_else()
    {
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var runner = new AuditingRunner(isolated: false);
        var proxies = AllProxies();
        var mgr = new RepositoryManager(runner, null, Path.Combine(_tmp, "wt"), runner.ManagerAgent, Path.Combine(_tmp, "mirrors"),
            sharedGroup: "agents", readEnvironment: Reader(proxies.Concat(Noise).ToDictionary(p => p.Key, p => p.Value)));

        var (success, error) = await mgr.CloneAsync(remote, Path.Combine(_tmp, "target"), TestContext.Current.CancellationToken);

        Assert.True(success, error);
        var orchestrator = runner.Calls.Where(c => c.Orchestrator).ToList();
        Assert.NotEmpty(orchestrator);
        foreach (var call in orchestrator)
        {
            Assert.True(call.Options?.CleanEnvironment);
            foreach (var (name, value) in proxies)
                Assert.Equal(value, call.Environment![name]);
            foreach (var name in Noise.Keys)
                Assert.DoesNotContain(name, call.Environment!.Keys);
        }
        foreach (var call in runner.Calls.Where(c => !c.Orchestrator))
            Assert.DoesNotContain(call.Environment?.Keys ?? [], k => RepositoryManager.ProxyEnvironmentKeys.Contains(k));
        runner.AssertClean();
    }
}
