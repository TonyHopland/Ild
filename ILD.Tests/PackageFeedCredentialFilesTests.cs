using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// What a run's processes are handed for the selected package feeds: an npm user
/// config authenticating every feed, the NuGet credential provider's endpoint list,
/// and a credential file that lives only as long as its owner and never where the
/// agent could change it.
/// </summary>
public sealed class PackageFeedCredentialFilesTests : IDisposable
{
    private const string UserConfig = "NPM_CONFIG_USERCONFIG";
    private const string NuGetEndpoints = "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS";

    private readonly string _sweepRoot = Directory.CreateTempSubdirectory("ild-feed-sweep-read-").FullName;
    private readonly string _sweepScratch = Directory.CreateTempSubdirectory("ild-feed-sweep-scratch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_sweepRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_sweepScratch, recursive: true); } catch { /* best effort */ }
    }

    internal static PackageFeedCredential Feed(string name, string feedUrl, string pat)
    {
        Assert.True(AzureFeedUrl.TryParse(feedUrl, out var url, out var problem), problem);
        return new PackageFeedCredential(name, url!, pat);
    }

    private static PackageFeedCredential OrgFeed(string pat = "pat-one-0001")
        => Feed("company", "https://pkgs.dev.azure.com/example-org/_packaging/company", pat);

    private static PackageFeedCredential ProjectFeed(string pat = "pat-two-0002")
        => Feed("tools", "https://pkgs.dev.azure.com/other-org/web/_packaging/tools/", pat);

    /// <summary>A project-scoped feed entered on the legacy host.</summary>
    private static PackageFeedCredential LegacyFeed(string pat = "pat-three-0003")
        => Feed("legacy", "https://third-org.pkgs.visualstudio.com/app/_packaging/legacy", pat);

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static string[] Lines(string path)
        => File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void The_npm_user_config_authenticates_every_feed_on_both_registry_paths_and_names_no_registry()
    {
        using var env = PackageFeedCredentialFiles.Materialize([OrgFeed(), ProjectFeed(), LegacyFeed()], "npmrc-test");

        var path = env.Environment[UserConfig];
        Assert.Equal(env.FilePath, path);
        var lines = Lines(path);

        // Every feed on both hosts, whichever one its URL was entered on.
        foreach (var (prefix, org, pat) in new[]
        {
            ("//pkgs.dev.azure.com/example-org/_packaging/company/npm/registry/", "example-org", "pat-one-0001"),
            ("//pkgs.dev.azure.com/example-org/_packaging/company/npm/", "example-org", "pat-one-0001"),
            ("//example-org.pkgs.visualstudio.com/_packaging/company/npm/registry/", "example-org", "pat-one-0001"),
            ("//example-org.pkgs.visualstudio.com/_packaging/company/npm/", "example-org", "pat-one-0001"),
            ("//pkgs.dev.azure.com/other-org/web/_packaging/tools/npm/registry/", "other-org", "pat-two-0002"),
            ("//pkgs.dev.azure.com/other-org/web/_packaging/tools/npm/", "other-org", "pat-two-0002"),
            ("//other-org.pkgs.visualstudio.com/web/_packaging/tools/npm/registry/", "other-org", "pat-two-0002"),
            ("//other-org.pkgs.visualstudio.com/web/_packaging/tools/npm/", "other-org", "pat-two-0002"),
            ("//pkgs.dev.azure.com/third-org/app/_packaging/legacy/npm/registry/", "third-org", "pat-three-0003"),
            ("//pkgs.dev.azure.com/third-org/app/_packaging/legacy/npm/", "third-org", "pat-three-0003"),
            ("//third-org.pkgs.visualstudio.com/app/_packaging/legacy/npm/registry/", "third-org", "pat-three-0003"),
            ("//third-org.pkgs.visualstudio.com/app/_packaging/legacy/npm/", "third-org", "pat-three-0003"),
        })
        {
            Assert.Contains($"{prefix}:username={org}", lines);
            Assert.Contains($"{prefix}:_password={B64(pat)}", lines);
            Assert.Contains($"{prefix}:email=npm@ild.local", lines);
        }

        // Where packages come from is the repository's own .npmrc's call.
        Assert.DoesNotContain(lines, l => l.StartsWith("registry", StringComparison.OrdinalIgnoreCase)
            || l.Contains(":registry", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Environment.Keys, k => k.Equals("npm_config_registry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_nuget_endpoints_name_each_feed_on_both_hosts_with_the_pat_json_escaped()
    {
        const string trickyPat = "pa\"t\\with/é\nnewline";
        using var env = PackageFeedCredentialFiles.Materialize([OrgFeed(trickyPat), ProjectFeed(), LegacyFeed()], "nuget-test");

        using var doc = JsonDocument.Parse(env.Environment[NuGetEndpoints]);
        var entries = doc.RootElement.GetProperty("endpointCredentials").EnumerateArray()
            .Select(e => (
                Endpoint: e.GetProperty("endpoint").GetString(),
                Username: e.GetProperty("username").GetString(),
                Password: e.GetProperty("password").GetString()))
            .OrderBy(e => e.Endpoint, StringComparer.Ordinal)
            .ToArray();

        var expected = new (string?, string?, string?)[]
        {
            ("https://example-org.pkgs.visualstudio.com/_packaging/company/nuget/v3/index.json", "ild", trickyPat),
            ("https://other-org.pkgs.visualstudio.com/web/_packaging/tools/nuget/v3/index.json", "ild", "pat-two-0002"),
            ("https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json", "ild", trickyPat),
            ("https://pkgs.dev.azure.com/other-org/web/_packaging/tools/nuget/v3/index.json", "ild", "pat-two-0002"),
            ("https://third-org.pkgs.visualstudio.com/app/_packaging/legacy/nuget/v3/index.json", "ild", "pat-three-0003"),
            ("https://pkgs.dev.azure.com/third-org/app/_packaging/legacy/nuget/v3/index.json", "ild", "pat-three-0003"),
        }.OrderBy(e => e.Item1, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, entries.Select(e => (e.Endpoint, e.Username, e.Password)).ToArray());
    }

    [Fact]
    public void Without_feeds_nothing_is_written_and_no_variable_is_set()
    {
        using var env = PackageFeedCredentialFiles.Materialize(Array.Empty<PackageFeedCredential>(), "none-test");

        Assert.Null(env.FilePath);
        Assert.Empty(env.Environment);
    }

    [Fact]
    public void The_credential_file_is_group_readable_only_in_a_directory_the_agent_cannot_write_and_outside_the_home()
    {
        if (OperatingSystem.IsWindows()) return;

        using var env = PackageFeedCredentialFiles.Materialize([OrgFeed()], "mode-test");
        var path = env.FilePath!;

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(path));
        var directory = Path.GetDirectoryName(path)!;
        Assert.Equal(0, (int)(File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)));

        // Single-uid mode: the agent read root's per-user fallback, which this
        // process created and owns.
        Assert.StartsWith(AgentIsolation.AgentReadRoot + Path.DirectorySeparatorChar, path);
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
            Assert.False(path.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                $"{path} is under the shared agent home");
    }

    [Fact]
    public void Each_owner_gets_its_own_file_and_disposing_one_removes_only_that_one()
    {
        var first = PackageFeedCredentialFiles.Materialize([OrgFeed()], "owner-test");
        using var second = PackageFeedCredentialFiles.Materialize([OrgFeed()], "owner-test");
        Assert.NotEqual(first.FilePath, second.FilePath);

        first.Dispose();
        first.Dispose();

        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
    }

    [Fact]
    public async Task Reclaiming_a_run_removes_its_credential_files_and_no_other_runs()
    {
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var runFile = PackageFeedCredentialFiles.Materialize([OrgFeed()], run.ToString("N"));
        using var otherFile = PackageFeedCredentialFiles.Materialize([OrgFeed()], other.ToString("N"));

        await AgentRunFiles.DeleteAsync(run, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(runFile.FilePath), "the reclaimed run's PAT file was left");
        Assert.True(File.Exists(otherFile.FilePath), "another run's PAT file was removed");
    }

    [Fact]
    public async Task The_startup_sweep_removes_credential_files_a_killed_process_left_and_keeps_fresh_ones()
    {
        var started = DateTime.UtcNow;
        // Where a live materialization puts its file, relative to the read root,
        // replayed under a private root so the sweep never touches parallel tests.
        string Plant(string prefix, DateTime writtenUtc)
        {
            using var live = PackageFeedCredentialFiles.Materialize([OrgFeed()], prefix);
            var planted = Path.Combine(_sweepRoot, Path.GetRelativePath(AgentIsolation.AgentReadRoot, live.FilePath!));
            Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
            File.Copy(live.FilePath!, planted);
            File.SetLastWriteTimeUtc(planted, writtenUtc);
            return planted;
        }

        var leftByCrash = Plant(Guid.NewGuid().ToString("N"), started.AddMinutes(-5));
        var leftPreview = Plant("preview", started.AddMinutes(-5));
        var launching = Plant(Guid.NewGuid().ToString("N"), started.AddSeconds(1));

        Assert.True(await AgentRunFiles.SweepAtStartupAsync(
            new HashSet<Guid>(), _sweepRoot, _sweepScratch, started, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(leftByCrash), "a PAT file a killed process left survived the sweep");
        Assert.False(File.Exists(leftPreview), "a preview's PAT file a killed process left survived the sweep");
        Assert.True(File.Exists(launching), "the sweep took the file of a launch that had just written it");
    }
}
