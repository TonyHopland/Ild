using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A preview's feed credential file exists only while something of the preview runs:
/// a runtime that is still registered but whose services have all gone keeps none.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public sealed class WorktreePreviewServicePackageFeedLifetimeTests : IDisposable
{
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-preview-feed-life-").FullName;
    private WorktreePreviewService? _service;

    public void Dispose()
    {
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers every health probe only once <paramref name="ready"/> says so, then gives up the start.</summary>
    private sealed class GiveUpWhenHandler(Func<bool> ready, CancellationTokenSource giveUp) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            while (!ready())
                await Task.Delay(20, cancellationToken);
            await giveUp.CancelAsync();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // The exit is reported on its own thread, a moment after the process is gone.
    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20, Ct);
        }
        return true;
    }

    [Fact]
    public async Task A_service_started_on_its_own_that_exits_before_it_is_healthy_leaves_no_file_behind()
    {
        var marker = Path.Combine(_worktree, "web.marker");
        var config = new
        {
            preview = new
            {
                defaultProfile = "app",
                profiles = new Dictionary<string, object>
                {
                    ["app"] = new
                    {
                        install = Array.Empty<object>(),
                        services = new[]
                        {
                            new
                            {
                                name = "web",
                                port = "web",
                                suggestedPort = FindFreePort(),
                                // A restore that fails: the service records its npm user
                                // config, marks itself done and exits without serving.
                                command = "printf '%s' \"$NPM_CONFIG_USERCONFIG\" > web.path; touch web.marker; exit 3",
                                healthUrl = "http://127.0.0.1:${PORT}/",
                            },
                        },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), JsonSerializer.Serialize(config));

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new GiveUpWhenHandler(() => File.Exists(marker), giveUp)));
        _service = new WorktreePreviewService(factory.Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance);
        var feeds = new[]
        {
            PackageFeedCredentialFilesTests.Feed("company", "https://pkgs.dev.azure.com/example-org/_packaging/company", "companyPAT-33cc"),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.StartServiceAsync(_worktree, "web", new WorktreePreviewStartOptions(PackageFeeds: feeds), giveUp.Token));

        // The runtime stays registered, with its exited service on show.
        Assert.True(_service.IsPreviewRunning(_worktree));
        var path = File.ReadAllText(Path.Combine(_worktree, "web.path"));
        Assert.False(string.IsNullOrEmpty(path), "the service got no npm user config");
        Assert.True(await EventuallyAsync(() => !File.Exists(path)), "a preview with nothing running kept its PAT file");
    }
}
