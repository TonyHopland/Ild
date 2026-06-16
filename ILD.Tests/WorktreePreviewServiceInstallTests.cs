using ILD.Core.Services.Implementations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Exercises the real <see cref="WorktreePreviewService.InstallAsync"/> path the
/// Start node uses when "Run ild.config install" is enabled — the executor tests
/// only mock the preview service, so the install runner itself is proven here.
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceInstallTests : IDisposable
{
    private readonly string _worktree;

    public WorktreePreviewServiceInstallTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-install-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService()
    {
        var factory = new Mock<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().Build();
        return new WorktreePreviewService(factory.Object, configuration, NullLogger<WorktreePreviewService>.Instance);
    }

    private void WriteConfig(string installCommand)
    {
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [
                  { "cwd": ".", "command": "{{installCommand}}" }
                ],
                "services": []
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    [Fact]
    public async Task InstallAsync_runs_default_profile_install_steps_in_the_worktree()
    {
        WriteConfig("printf done > install.marker");
        var service = BuildService();

        await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

        var marker = Path.Combine(_worktree, "install.marker");
        Assert.True(File.Exists(marker), "install step should have run in the worktree");
        Assert.Equal("done", File.ReadAllText(marker));
    }

    [Fact]
    public async Task InstallAsync_throws_when_an_install_step_exits_non_zero()
    {
        WriteConfig("exit 7");
        var service = BuildService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(_worktree, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_skips_best_effort_when_no_ild_config_is_present()
    {
        // No ild.config.json written — most projects ship none, so install must
        // skip best-effort rather than throw, reporting the reason for a warning.
        var service = BuildService();

        var result = await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("No ild.config.json", result.Message);
    }

    [Fact]
    public async Task InstallAsync_reports_installed_when_a_profile_is_present()
    {
        WriteConfig("true");
        var service = BuildService();

        var result = await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

        Assert.True(result.Installed);
    }

    [Fact]
    public async Task InstallAsync_exposes_npm_global_bin_on_the_host_process_path()
    {
        // npm install -g lands global CLIs in $HOME/.local/bin; the agents that
        // run after the Start node inherit the host process PATH, so install must
        // surface that directory there or the installed tools stay invisible.
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            // Pin HOME to a fresh temp dir so the expected bin path is deterministic
            // and provably absent from PATH before install runs.
            var home = Path.Combine(Path.GetTempPath(), "ild-install-home-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("HOME", home);
            var expectedBin = Path.Combine(home, ".local", "bin");
            Assert.DoesNotContain(
                expectedBin,
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));

            WriteConfig("true");
            var service = BuildService();

            await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Assert.Contains(expectedBin, path.Split(Path.PathSeparator));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Fact]
    public async Task InstallAsync_does_not_duplicate_npm_global_bin_on_repeated_installs()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            var home = Path.Combine(Path.GetTempPath(), "ild-install-home-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("HOME", home);
            var expectedBin = Path.Combine(home, ".local", "bin");

            WriteConfig("true");
            var service = BuildService();

            await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);
            await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

            var segments = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(segments, segment => segment == expectedBin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }
}
