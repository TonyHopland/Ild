using System.Net;
using System.Net.Sockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Pins the precedence a public service's advertised URL follows: an authored
/// <c>publicUrl</c> template, then the preview proxy origin, then the historical
/// direct host:port. The unset-proxy-base case is the important one — turning the
/// feature off has to leave every existing deployment's URLs exactly as they were.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public class WorktreePreviewServicePublicUrlTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    public WorktreePreviewServicePublicUrlTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-public-url-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService(string? proxyBase)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        _service = new WorktreePreviewService(
            factory.Object,
            new ConfigurationBuilder().Build(),
            PreviewProxyBase.Parse(proxyBase),
            NullLogger<WorktreePreviewService>.Instance, new PreviewDataClaims());
        return _service;
    }

    private void WriteConfig(string? publicUrlTemplate = null, string? secondPublicServiceName = null)
    {
        var command = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        var publicUrlLine = publicUrlTemplate == null ? "" : $"\"publicUrl\": \"{publicUrlTemplate}\",";
        var second = secondPublicServiceName == null
            ? ""
            : $$"""
              ,{
                    "name": "{{secondPublicServiceName}}",
                    "port": "second",
                    "suggestedPort": {{FindFreePort()}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "public": true
                  }
              """;
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "app",
                    "port": "frontend",
                    "suggestedPort": {{FindFreePort()}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    {{publicUrlLine}}
                    "public": true
                  }{{second}}
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    private static string? PublicUrl(
        string? proxyBase,
        string? authored = null,
        string? workItemId = "7",
        string serviceName = "app",
        bool singlePublicService = true,
        int? port = 5173)
        => WorktreePreviewService.ComposePublicUrl(
            authored, PreviewProxyBase.Parse(proxyBase), workItemId, serviceName, "127.0.0.1", port, singlePublicService);

    [Fact]
    public void Without_a_proxy_base_the_public_url_is_the_historical_host_and_port()
        => Assert.Equal("http://127.0.0.1:5173", PublicUrl(proxyBase: null));

    [Fact]
    public void With_a_proxy_base_the_public_url_becomes_the_work_items_preview_hostname()
        => Assert.Equal("http://wi-7.ild.example:8080", PublicUrl("http://ild.example:8080"));

    [Fact]
    public void A_proxy_base_on_its_scheme_default_port_advertises_no_port()
        => Assert.Equal("https://wi-7.ild.example.com", PublicUrl("https://ild.example.com"));

    [Fact]
    public void An_authored_public_url_wins_over_the_proxy_base()
        => Assert.Equal(
            "https://preview.example.test/app",
            PublicUrl("http://ild.example:8080", authored: "https://preview.example.test/app"));

    [Fact]
    public void Several_public_services_are_each_advertised_under_their_own_hostname()
    {
        // The bare wi-7 names one service, so neither may claim it.
        Assert.Equal("http://wi-7-app.ild.example:8080", PublicUrl("http://ild.example:8080", serviceName: "app", singlePublicService: false));
        Assert.Equal("http://wi-7-docs.ild.example:8080", PublicUrl("http://ild.example:8080", serviceName: "docs", singlePublicService: false));
    }

    [Fact]
    public void A_service_whose_name_cannot_be_a_hostname_keeps_a_reachable_direct_url()
    {
        // ild.config.json does not constrain service names to DNS labels, and an unreachable URL is worse than a loopback one.
        Assert.Equal(
            "http://127.0.0.1:5174",
            PublicUrl("http://ild.example:8080", serviceName: "docs_site", singlePublicService: false, port: 5174));
        Assert.Equal(
            "http://wi-7-app.ild.example:8080",
            PublicUrl("http://ild.example:8080", serviceName: "app", singlePublicService: false));
    }

    [Fact]
    public void Without_a_work_item_id_the_proxy_base_cannot_name_a_hostname_and_the_url_falls_back()
    {
        Assert.Equal("http://127.0.0.1:5173", PublicUrl("http://ild.example:8080", workItemId: null));
        Assert.Equal("http://127.0.0.1:5173", PublicUrl("http://ild.example:8080", workItemId: " "));
    }

    [Fact]
    public void Without_a_port_and_without_a_hostname_there_is_no_url()
        => Assert.Null(PublicUrl(proxyBase: null, port: null));

    [Theory]
    [InlineData("7", null, "wi-7")]
    [InlineData("7", "api", "wi-7-api")]
    [InlineData("7", "work-item-server", "wi-7-work-item-server")]
    [InlineData("7", "docs_site", null)]
    [InlineData("7", "-api", null)]
    [InlineData("7", "api-", null)]
    [InlineData(null, "api", null)]
    [InlineData("", null, null)]
    public void A_preview_host_label_needs_a_work_item_id_and_a_dns_safe_service_name(string? workItemId, string? serviceName, string? expected)
        => Assert.Equal(expected, WorktreePreviewService.BuildPreviewHostLabel(workItemId, serviceName));

    [Fact]
    public async Task A_started_preview_advertises_the_work_items_preview_hostname_when_a_proxy_base_is_set()
    {
        WriteConfig();
        var service = BuildService("http://ild.example:8080");

        var response = await service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(WorkItemId: "7"), TestContext.Current.CancellationToken);

        Assert.Equal("http://wi-7.ild.example:8080", response.Services.Single(s => s.Name == "app").PublicUrl);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
