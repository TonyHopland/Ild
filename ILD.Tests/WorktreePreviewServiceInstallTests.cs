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
    public async Task InstallAsync_throws_when_no_ild_config_is_present()
    {
        // No ild.config.json written — install must surface a clear failure.
        var service = BuildService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(_worktree, cancellationToken: CancellationToken.None));
    }
}
