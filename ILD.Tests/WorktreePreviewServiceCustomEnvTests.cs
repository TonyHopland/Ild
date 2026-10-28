using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Proves the repository's custom <c>.env</c> (see <c>Repository.PreviewEnv</c>)
/// reaches preview processes and that the documented precedence holds:
/// base defaults &lt; per-service <c>ild.config.json</c> env &lt; repo custom
/// <c>.env</c>, with the custom values injected verbatim rather than expanded as
/// templates. Exercised through the install path because install and service start both
/// resolve their environment through the same <c>BuildResolvedStep</c> merge — the
/// install step is a real preview process that writes what it actually saw to a
/// marker file, so the assertions observe the injected environment end-to-end.
/// </summary>
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
        return new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, new PreviewDataClaims());
    }

    // An install step that writes the value of an environment variable to a marker
    // file, so a test can read back exactly what the process saw. When
    // <paramref name="stepEnvJson"/> is supplied it becomes the step's own env block
    // (the per-service ild.config env the repo .env must win over).
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

        await service.InstallAsync(_worktree, customEnv: "API_TOKEN=s3cr3t\n# a comment\nOTHER=x", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("s3cr3t", ReadMarker());
    }

    private static Dictionary<string, string> Merge(
        Dictionary<string, string> defaults,
        Dictionary<string, string>? configEnv = null,
        Dictionary<string, string>? customEnv = null,
        Dictionary<string, string>? feedEnv = null)
        => WorktreePreviewService.MergePreviewEnvironment(
            defaults, configEnv, value => $"templated({value})", customEnv ?? [], feedEnv ?? []);

    private static Dictionary<string, string> Defaults(params (string Key, string Value)[] entries)
        => new(entries.ToDictionary(e => e.Key, e => e.Value), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Custom_env_overrides_a_base_default_variable()
    {
        var merged = Merge(
            Defaults(("NPM_CONFIG_CACHE", "/state/npm-cache"), ("HOME", "/home/x")),
            customEnv: new() { ["NPM_CONFIG_CACHE"] = "/tmp/from-dotenv-cache" });

        Assert.Equal("/tmp/from-dotenv-cache", merged["NPM_CONFIG_CACHE"]);
        Assert.Equal("/home/x", merged["HOME"]);
    }

    [Fact]
    public void Custom_env_overrides_per_service_config_env_which_overrides_the_defaults()
    {
        // The repo .env is what the human who owns the repository typed, and ild.config.json is worktree-editable.
        var merged = Merge(
            Defaults(("FOO", "from-default"), ("BAR", "from-default")),
            configEnv: new() { ["FOO"] = "from-config", ["BAR"] = "from-config", ["BAZ"] = "from-config" },
            customEnv: new() { ["FOO"] = "from-dotenv" });

        Assert.Equal("from-dotenv", merged["FOO"]);
        Assert.Equal("templated(from-config)", merged["BAR"]);
        Assert.Equal("templated(from-config)", merged["BAZ"]);
    }

    [Fact]
    public void Custom_env_values_are_injected_verbatim_not_expanded_as_templates()
    {
        // They are secrets, not ${PORT:...} templates: a password containing `${...}` must arrive byte-for-byte.
        const string password = "p$ss${STATE_DIR}w${PORT:nope}rd$";

        var merged = Merge(
            Defaults(),
            configEnv: new() { ["DB_PASSWORD"] = "from-config" },
            customEnv: new() { ["DB_PASSWORD"] = password });

        Assert.Equal(password, merged["DB_PASSWORD"]);
    }

    [Fact]
    public void Package_feed_credentials_beat_everything_typed_anywhere()
    {
        var merged = Merge(
            Defaults(("NPM_CONFIG_USERCONFIG", "/default")),
            configEnv: new() { ["NPM_CONFIG_USERCONFIG"] = "/config" },
            customEnv: new() { ["NPM_CONFIG_USERCONFIG"] = "/dotenv" },
            feedEnv: new() { ["NPM_CONFIG_USERCONFIG"] = "/feed" });

        Assert.Equal("/feed", merged["NPM_CONFIG_USERCONFIG"]);
    }

    [Fact]
    public async Task No_custom_env_is_a_no_op()
    {
        WriteConfig("API_TOKEN");
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: null, cancellationToken: TestContext.Current.CancellationToken);

        // The variable was never set, so the marker is empty.
        Assert.Equal(string.Empty, ReadMarker());
    }

    [Fact]
    public async Task Custom_env_is_injected_as_process_env_not_written_to_the_worktree()
    {
        // The secrets reach preview processes purely as environment variables; they
        // must never be materialised as a .env file in the worktree, where a run's
        // `git add -A` could push them into the PR. Use a no-op install step so the
        // only thing that could appear is a file the injection itself wrote.
        var config = """
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": { "app": { "install": [ { "cwd": ".", "command": "true" } ], "services": [] } }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
        var service = BuildService();

        await service.InstallAsync(_worktree, customEnv: "API_TOKEN=s3cr3t\nDB_PASSWORD=hunter2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_worktree, ".env")), ".env must not be written to the worktree");
        Assert.False(File.Exists(Path.Combine(_worktree, ".ild.env")), ".ild.env must not be written to the worktree");
        // No stray dotenv file of any name carries the secret into the worktree.
        // Read bytes and scan defensively so an unreadable/binary file can't make
        // the assertion throw instead of failing cleanly.
        var leaked = Directory.EnumerateFiles(_worktree, "*", SearchOption.AllDirectories)
            .Where(f => ContainsSecret(f, "s3cr3t"))
            .ToList();
        Assert.Empty(leaked);
    }

    private static bool ContainsSecret(string path, string secret)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(secret);
        }
        catch
        {
            return false;
        }
    }
}
