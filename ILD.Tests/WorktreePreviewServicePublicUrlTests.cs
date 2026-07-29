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
[Collection("EnvironmentPath")]
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

        var settings = new Dictionary<string, string?>();
        if (proxyBase != null)
            settings[PreviewProxyBase.ConfigurationKey] = proxyBase;

        _service = new WorktreePreviewService(
            factory.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    private void WriteConfig(string? publicUrlTemplate = null)
    {
        var command = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
        var publicUrlLine = publicUrlTemplate == null ? "" : $"\"publicUrl\": \"{publicUrlTemplate}\",";
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
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    [Fact]
    public async Task Without_a_proxy_base_the_public_url_is_the_historical_host_and_port()
    {
        WriteConfig();
        var service = BuildService(proxyBase: null);

        var response = await service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(WorkItemId: "7"));

        var app = response.Services.Single(s => s.Name == "app");
        Assert.Equal($"http://127.0.0.1:{app.Port}", app.PublicUrl);
    }

    [Fact]
    public async Task With_a_proxy_base_the_public_url_becomes_the_work_items_preview_hostname()
    {
        WriteConfig();
        var service = BuildService("http://ild.kube:8080");

        var response = await service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(WorkItemId: "7"));

        Assert.Equal("http://wi-7.ild.kube:8080", response.Services.Single(s => s.Name == "app").PublicUrl);
    }

    [Fact]
    public async Task A_proxy_base_on_its_scheme_default_port_advertises_no_port()
    {
        WriteConfig();
        var service = BuildService("https://ild.example.com");

        var response = await service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(WorkItemId: "7"));

        Assert.Equal("https://wi-7.ild.example.com", response.Services.Single(s => s.Name == "app").PublicUrl);
    }

    [Fact]
    public async Task An_authored_public_url_wins_over_the_proxy_base()
    {
        WriteConfig(publicUrlTemplate: "https://preview.example.test/app");
        var service = BuildService("http://ild.kube:8080");

        var response = await service.StartAsync(
            _worktree,
            new WorktreePreviewStartOptions(WorkItemId: "7"));

        Assert.Equal("https://preview.example.test/app", response.Services.Single(s => s.Name == "app").PublicUrl);
    }

    [Fact]
    public async Task Without_a_work_item_id_the_proxy_base_cannot_name_a_hostname_and_the_url_falls_back()
    {
        WriteConfig();
        var service = BuildService("http://ild.kube:8080");

        // Every API path that starts a preview knows the work item; a caller that
        // does not still gets a usable (if loopback-only) URL rather than a wrong one.
        var response = await service.StartAsync(_worktree);

        var app = response.Services.Single(s => s.Name == "app");
        Assert.Equal($"http://127.0.0.1:{app.Port}", app.PublicUrl);
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
