using ILD.Core.Services.Implementations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Proves the repository's custom <c>.env</c> (see <c>Repository.PreviewEnv</c>)
/// reaches preview processes and that the documented precedence holds:
/// base defaults &lt; repo custom <c>.env</c> &lt; per-service <c>ild.config.json</c>
/// env. Exercised through the install path because install and service start both
/// resolve their environment through the same <c>BuildResolvedStep</c> merge — the
/// install step is a real preview process that writes what it actually saw to a
/// marker file, so the assertions observe the injected environment end-to-end.
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceCustomEnvTests : IDisposable
{
    private readonly string _worktree;

    public WorktreePreviewServiceCustomEnvTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-customenv-tests-" + Guid.NewGuid().ToString("N"));
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

    // An install step that writes the value of an environment variable to a marker
    // file, so a test can read back exactly what the process saw. When
    // <paramref name="stepEnvJson"/> is supplied it becomes the step's own env block
    // (the per-service ild.config env that must win over the repo .env).
    private void WriteConfig(string varName, string? stepEnvJson = null)
    {
        var envClause = stepEnvJson is null ? string.Empty : $", \"env\": {stepEnvJson}";
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [
                  { "cwd": ".", "command": "printf '%s' \"${{varName}}\" > env.marker"{{envClause}} }
                ],
                "services": []
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private string ReadMarker() => File.ReadAllText(Path.Combine(_worktree, "env.marker"));

    [Fact]
    public async Task Custom_env_reaches_the_preview_process()
    {
        WriteConfig("API_TOKEN");
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: "API_TOKEN=s3cr3t\n# a comment\nOTHER=x");

        Assert.Equal("s3cr3t", ReadMarker());
    }

    [Fact]
    public async Task Custom_env_overrides_a_base_default_variable()
    {
        // NPM_CONFIG_CACHE is set by BuildDefaultEnvironment; the repo .env must win.
        WriteConfig("NPM_CONFIG_CACHE");
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: "NPM_CONFIG_CACHE=/tmp/from-dotenv-cache");

        Assert.Equal("/tmp/from-dotenv-cache", ReadMarker());
    }

    [Fact]
    public async Task Per_service_config_env_overrides_custom_env()
    {
        // The committed ild.config per-step env is the highest-precedence source.
        WriteConfig("FOO", stepEnvJson: "{ \"FOO\": \"from-config\" }");
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: "FOO=from-dotenv");

        Assert.Equal("from-config", ReadMarker());
    }

    [Fact]
    public async Task No_custom_env_is_a_no_op()
    {
        WriteConfig("API_TOKEN");
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: null);

        // The variable was never set, so the marker is empty.
        Assert.Equal(string.Empty, ReadMarker());
    }
}
