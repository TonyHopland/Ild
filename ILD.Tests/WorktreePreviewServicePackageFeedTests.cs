using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Preview processes — install steps and services — get the selected feeds'
/// credentials over anything the repository's custom <c>.env</c> says for the same
/// names. A preview's credential file lives exactly as long as its runtime: every
/// way a preview stops or fails to start takes it away. Each process writes what
/// it saw to a marker file, so the assertions observe the real environment.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public sealed class WorktreePreviewServicePackageFeedTests : IDisposable
{
    private const string CompanyPat = "companyPAT-22bb";
    private const string CompanyNuGet = "https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json";

    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-preview-feeds-").FullName;
    private WorktreePreviewService? _service;

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<PackageFeedCredential> Feeds()
        => [PackageFeedCredentialFilesTests.Feed("company", "https://pkgs.dev.azure.com/example-org/_packaging/company", CompanyPat)];

    private WorktreePreviewService BuildService()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        _service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A profile whose install step runs <paramref name="installCommand"/> and whose
    /// services each record the feed variables in <c>{name}.marker</c> before serving.
    /// </summary>
    private void WriteConfig(string? installCommand, params string[] serviceNames)
    {
        var services = serviceNames.Select(name => new
        {
            name,
            port = name,
            suggestedPort = FindFreePort(),
            command = $"printf '%s\\n%s' \"$NPM_CONFIG_USERCONFIG\" \"$VSS_NUGET_EXTERNAL_FEED_ENDPOINTS\" > {name}.marker; "
                + $"PORT=${{PORT}} exec node -e \"require('http').createServer((q,r)=>{{r.end('ok')}}).listen(process.env.PORT)\"",
            healthUrl = "http://127.0.0.1:${PORT}/",
        }).ToArray();
        var install = installCommand is null
            ? Array.Empty<object>()
            : new object[] { new { cwd = ".", command = installCommand } };
        var config = new
        {
            preview = new
            {
                defaultProfile = "app",
                profiles = new Dictionary<string, object> { ["app"] = new { install, services } },
            },
        };
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), JsonSerializer.Serialize(config));
    }

    private string[] Marker(string name) => File.ReadAllText(Path.Combine(_worktree, name)).Split('\n');

    private const string RecordInstall =
        "printf '%s\\n%s' \"$NPM_CONFIG_USERCONFIG\" \"$VSS_NUGET_EXTERNAL_FEED_ENDPOINTS\" > install.marker";

    private const string DotEnvNamingTheFeedVariables =
        "NPM_CONFIG_USERCONFIG=/tmp/from-dotenv.npmrc\nVSS_NUGET_EXTERNAL_FEED_ENDPOINTS=from-dotenv\nOTHER=kept";

    [Fact]
    public async Task An_install_step_gets_the_feed_values_over_the_custom_env_and_the_file_goes_with_the_install()
    {
        WriteConfig(RecordInstall + "; printf '%s' \"$OTHER\" > other.marker");

        await BuildService().InstallAsync(_worktree, customEnv: DotEnvNamingTheFeedVariables, packageFeeds: Feeds(), cancellationToken: Ct);

        var (path, endpoints) = (Marker("install.marker")[0], Marker("install.marker")[1]);
        Assert.NotEqual("/tmp/from-dotenv.npmrc", path);
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains(CompanyNuGet, endpoints);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(_worktree, "other.marker")));
        Assert.False(path.StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
        Assert.False(File.Exists(path), "the install's PAT file outlived the install");
    }

    [Fact]
    public async Task A_failing_install_leaves_no_file_behind()
    {
        WriteConfig(RecordInstall + "; exit 3");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            BuildService().InstallAsync(_worktree, packageFeeds: Feeds(), cancellationToken: Ct));

        var path = Marker("install.marker")[0];
        Assert.False(string.IsNullOrEmpty(path));
        Assert.False(File.Exists(path), "a failed install's PAT file was left");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_feeds_an_install_step_sees_the_custom_env_exactly_as_before(bool emptyList)
    {
        WriteConfig(RecordInstall);

        await BuildService().InstallAsync(_worktree, customEnv: DotEnvNamingTheFeedVariables,
            packageFeeds: emptyList ? Array.Empty<PackageFeedCredential>() : null, cancellationToken: Ct);

        Assert.Equal(new[] { "/tmp/from-dotenv.npmrc", "from-dotenv" }, Marker("install.marker"));
    }

    [Fact]
    public async Task Preview_services_and_install_get_the_feeds_over_the_custom_env_and_stopping_removes_the_file()
    {
        WriteConfig(RecordInstall, "web", "api");
        var service = BuildService();

        await service.StartAsync(_worktree,
            new WorktreePreviewStartOptions(CustomEnv: DotEnvNamingTheFeedVariables, PackageFeeds: Feeds()), Ct);

        var install = Marker("install.marker");
        Assert.NotEqual("/tmp/from-dotenv.npmrc", install[0]);
        Assert.Contains(CompanyNuGet, install[1]);
        var files = new[] { "web.marker", "api.marker" }.Select(m =>
        {
            var seen = Marker(m);
            Assert.NotEqual("/tmp/from-dotenv.npmrc", seen[0]);
            Assert.Contains(CompanyNuGet, seen[1]);
            Assert.True(File.Exists(seen[0]), $"{m}: the npm user config is not there while the preview runs");
            Assert.False(seen[0].StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
            return seen[0];
        }).Distinct().ToArray();

        await service.StopAsync(_worktree, Ct);

        foreach (var path in files)
            Assert.False(File.Exists(path), "a stopped preview's PAT file was left");
    }

    [Fact]
    public async Task Stopping_a_previews_last_service_removes_its_file()
    {
        WriteConfig(null, "web", "api");
        var service = BuildService();
        var options = new WorktreePreviewStartOptions(PackageFeeds: Feeds());

        await service.StartServiceAsync(_worktree, "web", options, Ct);
        await service.StartServiceAsync(_worktree, "api", options, Ct);
        var path = Marker("web.marker")[0];
        Assert.True(File.Exists(path));

        await service.StopServiceAsync(_worktree, "web", Ct);
        Assert.True(File.Exists(path), "the file went while a service of the preview still runs");

        await service.StopServiceAsync(_worktree, "api", Ct);
        Assert.False(File.Exists(path), "the PAT file outlived the preview's last service");
    }

    [Fact]
    public async Task Disposing_the_preview_service_removes_a_running_previews_file()
    {
        WriteConfig(null, "web");
        var service = BuildService();
        await service.StartAsync(_worktree, new WorktreePreviewStartOptions(PackageFeeds: Feeds()), Ct);
        var path = Marker("web.marker")[0];
        Assert.True(File.Exists(path));

        service.Dispose();
        _service = null;

        Assert.False(File.Exists(path), "the PAT file outlived the disposed preview service");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_preview_whose_install_fails_leaves_no_file_behind(bool startOneService)
    {
        WriteConfig(RecordInstall + "; exit 3", "web");
        var service = BuildService();
        var options = new WorktreePreviewStartOptions(PackageFeeds: Feeds());

        await Assert.ThrowsAnyAsync<Exception>(() => startOneService
            ? service.StartServiceAsync(_worktree, "web", options, Ct)
            : service.StartAsync(_worktree, options, Ct));

        var path = Marker("install.marker")[0];
        Assert.False(string.IsNullOrEmpty(path), "the preview's install step got no npm user config");
        Assert.False(File.Exists(path), "a preview that failed to start left its PAT file");
    }
}
