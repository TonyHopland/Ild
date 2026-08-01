using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Proves a Worktree Preview child inherits nothing from the orchestrator that it
/// was not deliberately given (ADR-0016). The commands come from the worktree's
/// <c>ild.config.json</c>, a file the agent writes and can trigger itself through
/// the ILD MCP tools, so the environment reaching them is a boundary, not a
/// convenience.
///
/// <para>
/// Both spawn sites are covered end-to-end, because inheriting the host process is
/// precisely the leak: the install runner and a long-running service are each real
/// preview processes dumping their own <c>env</c> to a marker file, with the
/// orchestrator's variables seeded on the test host first. The psi-level scrub is
/// pinned separately in <c>AgentIsolationTests</c>, which can also cover
/// <c>ILD_AGENT_USER</c> — the one variable that cannot be set process-wide here
/// without turning uid isolation on for the whole suite.
/// </para>
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceEnvironmentIsolationTests : IDisposable
{
    private const string SeededSecret = "ILD_DB_CONNECTION_STRING";

    private readonly string _worktree;
    private readonly string _outerRoot;
    private readonly EnvironmentScope _environment = new();
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceEnvironmentIsolationTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _worktree = Path.Combine(Path.GetTempPath(), "ild-envisolation-tests-" + id);
        _outerRoot = Path.Combine(Path.GetTempPath(), "ild-envisolation-outer-" + id);
        Directory.CreateDirectory(_worktree);
        Directory.CreateDirectory(_outerRoot);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        // Restore before deleting: the topology variables point into _outerRoot.
        _environment.Dispose();
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_outerRoot, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService()
    {
        // The health probe must really succeed, so back the factory with a live
        // HttpClient rather than a mock.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var configuration = new ConfigurationBuilder().Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    /// <summary>
    /// Stand the test host up as an orchestrator: every secret seeded, and every
    /// topology variable a single-uid host can carry pointed at directories this
    /// process owns. Returns the names that must not reach a preview child.
    /// </summary>
    private IReadOnlyList<string> SeedOrchestratorEnvironment()
    {
        var seeded = new List<string>();

        foreach (var key in AgentIsolation.SecretEnvironmentKeys)
        {
            _environment.Set(key, "orchestrator-" + key);
            seeded.Add(key);
        }

        foreach (var key in AgentIsolation.OrchestratorTopologyEnvKeys
                     .Where(key => key != AgentIsolation.AgentUserEnvVar))
        {
            // The roots move this preview's own state directory while they are set,
            // which is why they point somewhere real that the scope puts back.
            var value = key == AgentIsolation.AgentGroupEnvVar
                ? "outer-group"
                : Path.Combine(_outerRoot, key);
            if (key != AgentIsolation.AgentGroupEnvVar)
                Directory.CreateDirectory(value);

            _environment.Set(key, value);
            seeded.Add(key);
        }

        return seeded;
    }

    private void WriteInstallConfig(string? stepEnvJson = null)
    {
        var envClause = stepEnvJson is null ? string.Empty : $", \"env\": {stepEnvJson}";
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [
                  { "cwd": ".", "command": "env > install-env.marker"{{envClause}} }
                ],
                "services": []
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private Dictionary<string, string> ReadChildEnvironment(string markerName)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(_worktree, markerName)))
        {
            var separator = line.IndexOf('=');
            // Continuation lines of a multi-line value carry no leading NAME= and
            // are skipped; nothing seeded here is multi-line.
            if (separator > 0)
                environment.TryAdd(line[..separator], line[(separator + 1)..]);
        }
        return environment;
    }

    [Fact]
    public async Task Install_steps_inherit_no_orchestrator_secret_or_topology_variable()
    {
        var seeded = SeedOrchestratorEnvironment();
        WriteInstallConfig();

        await BuildService().InstallAsync(_worktree);

        var child = ReadChildEnvironment("install-env.marker");
        Assert.Equal(Array.Empty<string>(), seeded.Where(child.ContainsKey).ToArray());
    }

    [Fact]
    public async Task Services_inherit_no_orchestrator_secret_or_topology_variable()
    {
        var seeded = SeedOrchestratorEnvironment();
        WriteServiceConfig(FindFreePort());

        var started = await BuildService().StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);

        var child = ReadChildEnvironment("web-env.marker");
        Assert.Equal(Array.Empty<string>(), seeded.Where(child.ContainsKey).ToArray());
    }

    [Fact]
    public async Task A_variable_the_config_sets_back_survives_the_strip()
    {
        // The strip is about what was *inherited*. This repository's own profile
        // previews an ILD, which needs a database — so a preview must be able to
        // supply one of these names deliberately and have it survive, pointed at
        // its own infrastructure rather than the orchestrator's.
        SeedOrchestratorEnvironment();
        WriteInstallConfig(stepEnvJson: $$"""{ "{{SeededSecret}}": "Host=preview-db" }""");

        await BuildService().InstallAsync(_worktree);

        Assert.Equal("Host=preview-db", ReadChildEnvironment("install-env.marker")[SeededSecret]);
    }

    [Fact]
    public async Task A_variable_the_repository_preview_env_sets_back_survives_the_strip()
    {
        // Same guarantee one precedence level down: the repository's encrypted
        // preview .env is where the connection strings actually live, so the strip
        // must not out-rank it either.
        SeedOrchestratorEnvironment();
        WriteInstallConfig();

        await BuildService().InstallAsync(_worktree, customEnv: $"{SeededSecret}=Host=repo-db");

        Assert.Equal("Host=repo-db", ReadChildEnvironment("install-env.marker")[SeededSecret]);
    }

    [Fact]
    public async Task StopAsync_terminates_every_service()
    {
        WriteServiceConfig(FindFreePort(), FindFreePort());
        var service = BuildService();

        var started = await service.StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);

        var pids = new[] { ReadPid("web"), ReadPid("web2") };
        Assert.All(pids, pid => Assert.True(IsAlive(pid), $"service pid {pid} should be running before stop"));

        var stopped = await service.StopAsync(_worktree);
        Assert.Equal("stopped", stopped.State);

        // Kill(entireProcessTree) has to reach the node process behind the shell,
        // not just the shell — that is what the Preview tab's stop button relies
        // on, and under uid isolation it is what the orchestrator retains CAP_KILL
        // for.
        foreach (var pid in pids)
            Assert.True(await WaitForExitAsync(pid), $"service pid {pid} should be gone after stop");
    }

    // A service that records the environment it saw and its own pid, then serves
    // 200 on the health URL so StartAsync reaches "running".
    private void WriteServiceConfig(int port, int? secondPort = null)
    {
        var second = secondPort is null ? string.Empty : "," + ServiceJson("web2", secondPort.Value);
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [ {{ServiceJson("web", port)}}{{second}} ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private static string ServiceJson(string name, int port)
    {
        var listen = $"node -e \\\"require('fs').writeFileSync('{name}.pid', String(process.pid));"
            + "require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        return $$"""
        {
          "name": "{{name}}",
          "port": "{{name}}",
          "suggestedPort": {{port}},
          "command": "env > {{name}}-env.marker; PORT=${PORT} {{listen}}",
          "healthUrl": "http://127.0.0.1:${PORT}/"
        }
        """;
    }

    private int ReadPid(string serviceName)
        => int.Parse(File.ReadAllText(Path.Combine(_worktree, serviceName + ".pid")).Trim());

    private static async Task<bool> WaitForExitAsync(int pid)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(50);
        }
        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Sets process-global environment variables and puts them back on dispose.
    /// Safe only because the preview suites share the serialized
    /// <c>EnvironmentPath</c> collection, which already exists because
    /// <c>EnsureInstalledToolsOnProcessPath</c> mutates the host <c>PATH</c>.
    /// </summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public void Set(string name, string value)
        {
            _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
