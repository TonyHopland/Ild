using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Tools;

namespace ILD.Tests;

/// <summary>The Antigravity CLI is a downloaded release, not an npm package: installed from a manifest, checked against its SHA-512.</summary>
[Collection(ManagedAgentInstallsCollection.Name)]
public class ManagedAgentReleaseTests : IDisposable
{
    private const string ArchiveUrl = "https://downloads.example/agy-1.5.16.tar.gz";

    /// <summary>What the stand-in archive holds, here and in the installer script's tests: both must unpack the same file.</summary>
    internal const string AgentScript = "#!/bin/sh\necho agy\n";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "ild-agent-release-" + Guid.NewGuid().ToString("N"));
    private readonly ManagedAgent _agent = ManagedAgentCatalog.Agy;
    private readonly Downloads _downloads = new();

    public ManagedAgentReleaseTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Runner : IProcessRunner
    {
        public List<string> Files { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
        {
            Files.Add(fileName);
            return Task.FromResult(File.Exists(fileName) ? new ProcessResult(0, "1.5.16\n", "") : new ProcessResult(127, "", "not found"));
        }
    }

    private sealed class Downloads : HttpMessageHandler
    {
        public byte[] Archive { get; set; } = TarGz("antigravity", AgentScript);
        public string? ManifestVersion { get; set; } = "1.5.16";
        public string? ManifestSha512 { get; set; }
        public HttpStatusCode ManifestStatus { get; set; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
            {
                if (ManifestStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(ManifestStatus));
                var sha = ManifestSha512 ?? Convert.ToHexStringLower(SHA512.HashData(Archive));
                var json = $$"""{"version":"{{ManifestVersion}}","url":"{{ArchiveUrl}}","sha512":"{{sha}}"}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(request.RequestUri.ToString() == ArchiveUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    internal static byte[] TarGz(string entryName, string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        return output.ToArray();
    }

    private ManagedAgentService Service(Runner? runner = null)
        => new(new HttpClient(_downloads), runner ?? new Runner(), _dataRoot, logger: null);

    // The same table as the installer script's, in RunToolStoreScriptTests: ILD's own machine and a run host name platforms alike.
    [Theory]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.X64, false, "linux_amd64")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.Arm64, false, "linux_arm64")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.X64, true, "linux_amd64_musl")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.Arm64, true, "linux_arm64_musl")]
    [InlineData("darwin", System.Runtime.InteropServices.Architecture.Arm64, false, "darwin_arm64")]
    [InlineData("darwin", System.Runtime.InteropServices.Architecture.X64, true, "darwin_amd64")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.RiscV64, false, null)]
    [InlineData(null, System.Runtime.InteropServices.Architecture.X64, false, null)]
    public void Platforms_are_named_by_system_architecture_and_c_library(string? os, System.Runtime.InteropServices.Architecture architecture, bool musl, string? expected)
        => Assert.Equal(expected, ManagedAgentService.ReleasePlatformName(os, architecture, musl));

    [Fact]
    public async Task The_latest_version_is_the_manifests_and_a_missing_install_is_actionable()
    {
        var status = await Service().GetStatusAsync(_agent, TestContext.Current.CancellationToken);

        Assert.Null(status.NpmPackage);
        Assert.Null(status.InstalledVersion);
        Assert.Equal("1.5.16", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Contains(_downloads.Requests, url => url.StartsWith("https://antigravity-cli-auto-updater", StringComparison.Ordinal) && url.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreachable_release_server_is_reported_and_offers_nothing()
    {
        _downloads.ManifestStatus = HttpStatusCode.ServiceUnavailable;

        var status = await Service().GetStatusAsync(_agent, TestContext.Current.CancellationToken);

        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.Error);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Update_downloads_the_release_into_a_version_dir_and_makes_it_the_active_binary()
    {
        var service = Service();

        var status = await service.UpdateAsync("agy", TestContext.Current.CancellationToken);

        var versionId = ManagedAgentInstall.CurrentVersionId(_dataRoot, _agent)!;
        var binary = ManagedAgentInstall.BinaryIn(ManagedAgentInstall.VersionDir(_dataRoot, _agent, versionId), _agent);
        Assert.Equal(binary, ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
        Assert.Equal(AgentScript, File.ReadAllText(binary));
        Assert.Equal(ManagedAgentService.ExecutableMode, File.GetUnixFileMode(binary));
        Assert.Equal("1.5.16", status.InstalledVersion);
        Assert.False(status.UpdateAvailable);
        Assert.False(File.Exists(Path.Combine(ManagedAgentInstall.VersionDir(_dataRoot, _agent, versionId), "release.tar.gz")));
    }

    [Fact]
    public async Task An_archive_that_does_not_match_its_checksum_installs_nothing()
    {
        _downloads.ManifestSha512 = new string('0', 128);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().UpdateAsync("agy", TestContext.Current.CancellationToken));

        Assert.Contains("checksum", failure.Message);
        Assert.Null(ManagedAgentInstall.CurrentVersionId(_dataRoot, _agent));
        Assert.Empty(Directory.EnumerateDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)));
    }

    [Fact]
    public async Task An_archive_without_the_executable_installs_nothing()
    {
        _downloads.Archive = TarGz("something-else", "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().UpdateAsync("agy", TestContext.Current.CancellationToken));

        Assert.Null(ManagedAgentInstall.CurrentVersionId(_dataRoot, _agent));
        Assert.Empty(Directory.EnumerateDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)));
    }

    [Fact]
    public async Task A_manifest_names_only_the_newest_build_so_an_older_version_cannot_be_installed()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().AutoUpdateAsync("agy", "1.5.15", TestContext.Current.CancellationToken));

        Assert.Contains("1.5.16", failure.Message);
        Assert.Null(ManagedAgentInstall.CurrentVersionId(_dataRoot, _agent));
        Assert.Empty(Directory.EnumerateDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)));
    }

    [Fact]
    public async Task Auto_update_has_no_plan_because_a_manifest_carries_no_release_dates()
        => Assert.Null(await Service().GetAutoUpdatePlanAsync("agy", TimeSpan.FromDays(3), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_second_update_keeps_the_first_as_the_rollback_target_only_when_it_is_another_version()
    {
        var service = Service();
        await service.UpdateAsync("agy", TestContext.Current.CancellationToken);
        _downloads.ManifestVersion = "1.6.0";

        var status = await service.UpdateAsync("agy", TestContext.Current.CancellationToken);

        Assert.Equal("1.5.16", status.PreviousVersion);
    }

    [Fact]
    public async Task A_run_in_its_own_container_is_given_the_manifest_to_read_for_its_own_platform()
    {
        await Service().UpdateAsync("agy", TestContext.Current.CancellationToken);

        var installed = Assert.Single(RunAgent.Installed(_dataRoot));

        Assert.Equal("1.5.16", installed.Version);
        Assert.Equal(ManagedAgentCatalog.Agy.Release!.ManifestUrl, installed.ReleaseManifest);
        var argument = Assert.Single(RunToolStore.Arguments([], [installed]));
        Assert.StartsWith("release={\"key\":\"agy\",\"manifest\":", argument);
        Assert.Contains("{platform}", argument);
        Assert.DoesNotContain(ArchiveUrl, argument);
    }

    [Fact]
    public async Task The_manifest_address_can_be_set_in_the_environment_and_is_the_one_a_run_is_given()
    {
        var environment = new TestProcessEnvironment { { "ILD_AGY_RELEASE_MANIFEST_URL", "https://mirror.example/agy/{platform}.json" } };
        var service = new ManagedAgentService(new HttpClient(_downloads), new Runner(), _dataRoot, logger: null, environment: environment);

        await service.UpdateAsync("agy", TestContext.Current.CancellationToken);

        Assert.All(_downloads.Requests.Where(url => url.EndsWith(".json", StringComparison.Ordinal)), url => Assert.StartsWith("https://mirror.example/agy/", url));
        Assert.Equal("https://mirror.example/agy/{platform}.json", Assert.Single(RunAgent.Installed(_dataRoot)).ReleaseManifest);
    }

    [Fact]
    public async Task Rolling_back_switches_to_the_kept_install_without_going_to_the_release_server()
    {
        var service = Service();
        await service.UpdateAsync("agy", TestContext.Current.CancellationToken);
        _downloads.ManifestVersion = "1.6.0";
        await service.UpdateAsync("agy", TestContext.Current.CancellationToken);
        var requests = _downloads.Requests.Count;

        var status = await service.RollbackAsync("agy", "1.5.16", TestContext.Current.CancellationToken);

        Assert.Equal("1.5.16", Assert.Single(RunAgent.Installed(_dataRoot)).Version);
        Assert.Equal("1.6.0", status.HeldVersion);
        Assert.Null(status.PreviousVersion);
        // Only the status check's own look at the manifest: the rollback itself downloads nothing.
        Assert.Equal(requests + 1, _downloads.Requests.Count);
        Assert.Null(await service.GetAutoUpdatePlanAsync("agy", TimeSpan.Zero, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }
}
