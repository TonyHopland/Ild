using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>What a Worktree Preview's command is launched with (ADR-0016): the command as written, and an environment of its own.</summary>
public class WorktreePreviewServiceLaunchTests : IDisposable
{
    private readonly string _stateDirectory;
    private readonly TestProcessEnvironment _environment = new TestProcessEnvironment().WithRealPath();
    private string? _redirectedHome;

    public WorktreePreviewServiceLaunchTests()
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

    private WorktreePreviewService BuildService(string? egressProxy = null)
        => new(new Mock<IHttpClientFactory>().Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, egressProxy, environment: _environment);

    private WorktreePreviewService.ResolvedStep Step(NetworkMode mode = NetworkMode.Monitor) => new(
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
        var psi = BuildService().BuildPreviewProcess(Step());

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor, true)]
    [InlineData(NetworkMode.Off, false)]
    public void A_preview_is_proxied_in_every_mode_but_Off(NetworkMode mode, bool proxied)
    {
        var psi = BuildService("http://127.0.0.1:3128").BuildPreviewProcess(Step(mode));

        Assert.Equal(proxied, psi.Environment.TryGetValue("HTTPS_PROXY", out var proxy) && proxy == "http://127.0.0.1:3128");
    }

    [Fact]
    public void A_preview_gets_the_build_server_defaults_and_its_own_env_overrides_them()
    {
        var step = new WorktreePreviewService.ResolvedStep(
            "dotnet run",
            _stateDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["UseSharedCompilation"] = "true" });

        var psi = BuildService().BuildPreviewProcess(step);

        Assert.Equal("true", psi.Environment["UseSharedCompilation"]);
        Assert.Equal("1", psi.Environment["MSBUILDDISABLENODEREUSE"]);
    }

    [Fact]
    public void The_npm_prefix_is_under_our_home_and_is_created()
    {
        var home = RedirectOrchestratorHome();

        var environment = BuildService().BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(home, environment["HOME"]);
        Assert.Equal(Path.Combine(home, ".local"), environment["NPM_CONFIG_PREFIX"]);
        Assert.Contains(Path.Combine(home, ".local", "bin"), environment["PATH"].Split(Path.PathSeparator));
        Assert.True(Directory.Exists(Path.Combine(home, ".local", "bin")));
    }

    [Fact]
    public void The_npm_cache_stays_in_the_state_directory()
    {
        var environment = BuildService().BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(Path.Combine(_stateDirectory, "npm-cache"), environment["NPM_CONFIG_CACHE"]);
    }
}
