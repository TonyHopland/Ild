using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Proves a Worktree Preview runs as the agent uid, and that everything it needs
/// to write follows it there (ADR-0016).
///
/// <para>
/// The crossing is driven through the explicit-parameter constructor rather than
/// the process-global <c>ILD_AGENT_USER</c>, which would turn isolation on for
/// every other test in the host. The <c>setpriv</c> the crossing execs is a shim on
/// <c>PATH</c> — <c>AgentIsolation</c> resolves the bare name deliberately, and a
/// real <c>setpriv --reuid</c> needs <c>CAP_SETUID</c> and a second uid, neither of
/// which a test host has. The shim records the arguments it was handed and then
/// execs the wrapped command unchanged, so the assertions cover both halves: that
/// the preview is routed, and what the routed child actually saw.
/// </para>
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceAgentUidTests : IDisposable
{
    private const string AgentUser = "agent";

    private readonly string _worktree;
    private readonly string _agentHome;
    private readonly string _shimDirectory;
    private readonly string _shimLog;
    private readonly string? _originalPath;
    private readonly string? _originalHome;
    private string? _redirectedHome;
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceAgentUidTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _worktree = Path.Combine(Path.GetTempPath(), "ild-agentuid-tests-" + id);
        _agentHome = Path.Combine(Path.GetTempPath(), "ild-agentuid-home-" + id);
        _shimDirectory = Path.Combine(Path.GetTempPath(), "ild-agentuid-shim-" + id);
        _shimLog = Path.Combine(_shimDirectory, "setpriv-args");
        Directory.CreateDirectory(_worktree);
        Directory.CreateDirectory(_agentHome);
        Directory.CreateDirectory(_shimDirectory);

        _originalPath = Environment.GetEnvironmentVariable("PATH");
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        InstallSetprivShim();
    }

    /// <summary>
    /// Point the orchestrator's own <c>HOME</c> at an empty directory for the
    /// duration of one test. Process-global, and safe only inside the serialized
    /// <c>EnvironmentPath</c> collection — the same reason the shim can live on
    /// <c>PATH</c>.
    /// </summary>
    private string RedirectOrchestratorHome()
    {
        _redirectedHome = Path.Combine(Path.GetTempPath(), "ild-agentuid-orchhome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_redirectedHome);
        Environment.SetEnvironmentVariable("HOME", _redirectedHome);
        return _redirectedHome;
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        foreach (var directory in new[] { _worktree, _agentHome, _shimDirectory, _redirectedHome })
        {
            if (directory is null) continue;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    // Records the argv it was called with, then drops the setpriv options up to the
    // "--" terminator and execs the real command as the current user.
    private void InstallSetprivShim()
    {
        var shim = Path.Combine(_shimDirectory, "setpriv");
        File.WriteAllText(shim, $"""
        #!/bin/sh
        printf '%s\n' "$@" >> '{_shimLog}'
        while [ "$1" != "--" ]; do shift; done
        shift
        exec "$@"

        """);
        File.SetUnixFileMode(shim,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Environment.SetEnvironmentVariable("PATH", _shimDirectory + Path.PathSeparator + _originalPath);
    }

    private string[] ShimArguments()
        => File.Exists(_shimLog) ? File.ReadAllLines(_shimLog) : Array.Empty<string>();

    private WorktreePreviewService BuildService(string? agentUser = AgentUser, bool withAgentHome = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var configuration = new ConfigurationBuilder().Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            agentUser, agentGroup: "ild-agents", agentHome: withAgentHome ? _agentHome : null);
        return _service;
    }

    private void WriteInstallConfig()
    {
        var config = """
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [ { "cwd": ".", "command": "env > install-env.marker" } ],
                "services": []
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private Dictionary<string, string> ReadChildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(_worktree, "install-env.marker")))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                environment.TryAdd(line[..separator], line[(separator + 1)..]);
        }
        return environment;
    }

    [Fact]
    public async Task Install_steps_cross_to_the_agent_uid()
    {
        WriteInstallConfig();

        await BuildService().InstallAsync(_worktree);

        // --reuid is the whole difference from the capability-only wrap the preview
        // used to get: the preview and the agent's own builds in the same worktree
        // are now one uid, which is what removes the MSB3021/MSB3374 class of
        // failure on files the other side owns.
        Assert.Contains("--reuid=" + AgentUser, ShimArguments());
        Assert.Contains("--regid=ild-agents", ShimArguments());
        Assert.Contains("--init-groups", ShimArguments());
        Assert.Contains("--ambient-caps=-all", ShimArguments());
    }

    [Fact]
    public async Task Services_cross_to_the_agent_uid()
    {
        WriteServiceConfig(FindFreePort());

        var started = await BuildService().StartAsync(_worktree, cancellationToken: CancellationToken.None);

        Assert.Equal("running", started.State);
        Assert.Contains("--reuid=" + AgentUser, ShimArguments());
    }

    [Fact]
    public async Task Nothing_is_routed_when_isolation_is_off()
    {
        // The documented escape hatch: ILD_AGENT_USER unset means the command runs
        // inline as the current user, exactly as before uid isolation existed.
        WriteInstallConfig();

        await BuildService(agentUser: null).InstallAsync(_worktree);

        Assert.Empty(ShimArguments());
    }

    [Fact]
    public async Task The_npm_prefix_follows_the_agent_home()
    {
        // $HOME/.local is where `npm install -g` puts a global CLI, and the agent
        // uid is both what runs the install and what execs the result afterwards.
        // Pointing it at the orchestrator's home instead leaves the install writing
        // into a 0710 directory it cannot enter.
        WriteInstallConfig();

        await BuildService().InstallAsync(_worktree);

        var child = ReadChildEnvironment();
        Assert.Equal(_agentHome, child["HOME"]);
        Assert.Equal(Path.Combine(_agentHome, ".local"), child["NPM_CONFIG_PREFIX"]);
        Assert.Contains(Path.Combine(_agentHome, ".local", "bin"), child["PATH"].Split(Path.PathSeparator));
    }

    [Fact]
    public async Task The_orchestrator_creates_nothing_inside_the_agent_home()
    {
        // A prefix directory the orchestrator created would be owned by the
        // orchestrator, in a home whose group the agent is not in, and the agent's
        // own `npm install -g` would fail on it. The entrypoint provisions this
        // scaffolding as the agent instead.
        WriteInstallConfig();

        await BuildService().InstallAsync(_worktree);

        Assert.False(Directory.Exists(Path.Combine(_agentHome, ".local")),
            "the orchestrator must not create the npm prefix inside the agent's home");
    }

    [Fact]
    public async Task A_crossing_with_no_agent_home_keeps_the_prefix_where_it_can_create_it()
    {
        // An agent user without an agent home is a shape the crossing explicitly
        // allows — Route leaves HOME as-is when the home is null, and the
        // entrypoint would export exactly this if AGENT_HOME were cleared. The
        // prefix then falls back to the orchestrator's own home, and the
        // orchestrator both may and must create it: the two questions "is
        // isolation on" and "is this path inside the agent's home" come apart
        // here, and keying the skip on the former left npm install -g with no
        // prefix and EnsureInstalledToolsOnProcessPath advertising a directory
        // that did not exist.
        WriteInstallConfig();
        // Redirect HOME to somewhere empty, so "the prefix was created" is a real
        // assertion rather than one the test host's own ~/.local/bin satisfies.
        var orchestratorHome = RedirectOrchestratorHome();

        await BuildService(withAgentHome: false).InstallAsync(_worktree);

        var child = ReadChildEnvironment();
        Assert.Equal(orchestratorHome, child["HOME"]);
        Assert.Equal(Path.Combine(orchestratorHome, ".local"), child["NPM_CONFIG_PREFIX"]);
        Assert.True(Directory.Exists(Path.Combine(orchestratorHome, ".local", "bin")),
            "the prefix outside the agent's home must still be created");

        // Still routed — only the HOME half of the crossing is absent.
        Assert.Contains("--reuid=" + AgentUser, ShimArguments());
    }

    [Fact]
    public async Task Preview_state_lives_under_the_shared_scratch_root()
    {
        // Logs and the npm cache are written by both uids, so they belong in the
        // setgid shared-group tree, not the orchestrator-private root the preview
        // used while its steps still ran as the orchestrator.
        WriteServiceConfig(FindFreePort());
        var service = BuildService();
        var started = await service.StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);

        var status = await service.GetStatusAsync(_worktree);

        Assert.NotNull(status.StateDirectory);
        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(AgentIsolation.ScratchRoot) + Path.DirectorySeparatorChar,
            status.StateDirectory);
        Assert.False(status.StateDirectory!.StartsWith(
            Path.TrimEndingDirectorySeparator(AgentIsolation.PrivateRoot) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_orchestrator_can_still_read_a_routed_services_log()
    {
        // get_preview_logs and the Preview tab's Log column read these files as the
        // orchestrator while the service that produced them runs as the agent, so
        // the move to the shared root has to keep that readable.
        WriteServiceConfig(FindFreePort());
        var service = BuildService();
        var started = await service.StartAsync(_worktree, cancellationToken: CancellationToken.None);
        Assert.Equal("running", started.State);

        var log = await service.GetServiceLogAsync(_worktree, "web");

        Assert.NotNull(log);
        Assert.Contains("node -e", log);
    }

    private void WriteServiceConfig(int port)
    {
        var listen = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "web",
                    "port": "web",
                    "suggestedPort": {{port}},
                    "command": "PORT=${PORT} {{listen}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
