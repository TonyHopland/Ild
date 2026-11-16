using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>What a Worktree Preview's command is launched with (ADR-0016): the command as written, and an environment of its own.</summary>
public class PreviewLauncherTests : IDisposable
{
    private readonly string _stateDirectory;
    private readonly TestProcessEnvironment _environment = new TestProcessEnvironment().WithRealPath();
    private string? _redirectedHome;

    public PreviewLauncherTests()
    {
        _stateDirectory = Path.Combine(Path.GetTempPath(), "ild-launch-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _stateDirectory, _redirectedHome })
        {
            if (directory is null) continue;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private PreviewLauncher BuildLauncher(EgressProxyOptions? egressProxy = null)
        => new(LocalRunHost.Shared, egressProxy, EgressProxyTests.Creds, _environment,
            new PreviewNotifier(null, NullLogger.Instance), new PreviewTimings(), NullLogger.Instance);

    private PreviewLauncher.ResolvedStep Step(NetworkMode mode = NetworkMode.Monitor) => new(
        "npm run dev",
        _stateDirectory,
        new Dictionary<string, string>(StringComparer.Ordinal),
        mode);

    private string RedirectOrchestratorHome()
    {
        _redirectedHome = Path.Combine(Path.GetTempPath(), "ild-launch-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_redirectedHome);
        _environment.Set("HOME", _redirectedHome);
        return _redirectedHome;
    }

    [Fact]
    public void A_preview_command_runs_as_written_in_a_login_shell()
    {
        var psi = BuildLauncher().BuildPreviewProcess(Step());

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor, true)]
    [InlineData(NetworkMode.Off, false)]
    public void A_preview_is_proxied_in_every_mode_but_Off(NetworkMode mode, bool proxied)
    {
        var launcher = BuildLauncher(new EgressProxyOptions(true, 3128));
        var psi = LocalRunHost.Launch(launcher.PreparedCommand(Step(mode)), EgressProxyTests.Creds);

        Assert.Equal(proxied, psi.Environment.TryGetValue("HTTPS_PROXY", out var proxy));
        if (!proxied) return;
        Assert.True(AgentIsolationEgressTests.TryReadUrl(EgressProxyTests.Creds, proxy!, out var scope));
        Assert.Equal(LaunchKind.Preview, scope.Kind);
        Assert.EndsWith("@127.0.0.1:3128", proxy);
    }

    [Fact]
    public void A_preview_gets_the_build_server_defaults_and_its_own_env_overrides_them()
    {
        var step = new PreviewLauncher.ResolvedStep(
            "dotnet run",
            _stateDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["UseSharedCompilation"] = "true" });

        var psi = LocalRunHost.Launch(BuildLauncher().PreparedCommand(step), EgressProxyTests.Creds);

        Assert.Equal("true", psi.Environment["UseSharedCompilation"]);
        Assert.Equal("1", psi.Environment["MSBUILDDISABLENODEREUSE"]);
    }

    [Fact]
    public void A_step_leaves_home_path_and_the_npm_prefix_to_where_it_runs()
    {
        RedirectOrchestratorHome();

        var environment = PreviewLauncher.BuildDefaultEnvironment(_stateDirectory);

        // Ours would name directories a run container does not have, and hide the ones it does.
        Assert.DoesNotContain("HOME", environment.Keys);
        Assert.DoesNotContain("PATH", environment.Keys);
        Assert.DoesNotContain("NPM_CONFIG_PREFIX", environment.Keys);
    }

    [Fact]
    public void The_npm_cache_stays_in_the_state_directory()
    {
        var environment = PreviewLauncher.BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(Path.Combine(_stateDirectory, "npm-cache"), environment["NPM_CONFIG_CACHE"]);
    }
}
