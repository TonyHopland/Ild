using System.Net;
using System.Net.Sockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Covers <see cref="WorktreePreviewService.ResolvePreviewTargetAsync"/> — the
/// chain that turns a preview hostname's label into the loopback port the proxy
/// forwards to. Every rung of that chain has its own way of coming up empty, and
/// each produces a distinct outcome the proxy renders as its own page, so all of
/// them are pinned here alongside the happy paths.
/// </summary>
[Collection(PreviewPortsCollection.Name)]
public class WorktreePreviewServiceProxyTargetTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    public WorktreePreviewServiceProxyTargetTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-proxy-target-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        _service = new WorktreePreviewService(
            factory.Object,
            new ConfigurationBuilder().Build(),
            PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance, new PreviewDataClaims());
        return _service;
    }

    /// <summary>A work item manager that hands back one item, however it is asked.</summary>
    private static IWorkItemManager WorkItemsReturning(WorkItemView? view)
    {
        var manager = new Mock<IWorkItemManager>();
        manager.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(view);
        return manager.Object;
    }

    private IWorkItemManager WorkItemsPointingAtThisWorktree()
        => WorkItemsReturning(new WorkItemView { Id = "7", WorktreePath = _worktree });

    /// <summary>
    /// Three services: the public one the bare <c>wi-{id}</c> form resolves to, a
    /// plain named one, and one whose name contains hyphens — the case that breaks
    /// any parser splitting the label on its first hyphen.
    /// </summary>
    private void WriteConfig(bool rewriteHostOnApp = true, string? alsoPublic = null)
    {
        var command = "node -e \\\"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\\\"";
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
                    "public": true,
                    "rewriteHost": {{(rewriteHostOnApp ? "true" : "false")}}
                  },
                  {
                    "name": "api",
                    "port": "backend",
                    "suggestedPort": {{FindFreePort()}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "public": {{(alsoPublic == "api" ? "true" : "false")}}
                  },
                  {
                    "name": "work-item-server",
                    "port": "workitems",
                    "suggestedPort": {{FindFreePort()}},
                    "command": "PORT=${PORT} {{command}}",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
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
    public async Task A_started_preview_resolves_a_label_to_the_running_services_port()
    {
        WriteConfig();
        var service = BuildService();
        var started = await service.StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);
        var appPort = started.Services.Single(s => s.Name == "app").Port;

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree(), TestContext.Current.CancellationToken);

        Assert.Equal(PreviewTargetOutcome.Resolved, target.Outcome);
        Assert.True(target.IsResolved);
        Assert.Equal(appPort, target.Port);
        Assert.Equal("app", target.ServiceName);
        // Host rewriting is on unless a service opts out, because host-checking dev
        // servers reject the preview hostname outright.
        Assert.True(target.RewriteHost);
    }

    [Theory]
    [InlineData("wi-")]          // no id at all
    [InlineData("wi-abc")]       // ids in preview hostnames are numeric
    [InlineData("wi-7-")]        // trailing separator, no service name
    [InlineData("api")]          // not a preview label
    [InlineData("")]
    public async Task Labels_that_are_not_preview_hostnames_are_rejected_before_any_lookup(string label)
    {
        var service = BuildService();
        var workItems = new Mock<IWorkItemManager>(MockBehavior.Strict);

        var target = await service.ResolvePreviewTargetAsync(label, workItems.Object, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewTargetOutcome.NotAPreviewHost, target.Outcome);
        Assert.False(target.IsResolved);
        // Strict mock: rejecting the label must not have cost a work-item lookup.
        workItems.VerifyNoOtherCalls();
    }

    private static WorktreePreviewService.PreviewServiceRow Row(
        string name, bool isPublic = false, bool running = true, int port = 0, bool rewriteHost = true)
        => new(name, isPublic, rewriteHost, running, port == 0 ? 4000 : port);

    [Theory]
    [InlineData("wi-7", "7", null)]
    [InlineData("wi-7-api", "7", "api")]
    [InlineData("WI-7-API", "7", "API")]
    [InlineData("  wi-12  ", "12", null)]
    // A service name with hyphens is the service's, not part of the id.
    [InlineData("wi-12-work-item-server", "12", "work-item-server")]
    public void A_preview_host_label_is_a_numeric_work_item_id_and_an_optional_service_name(string label, string id, string? service)
    {
        Assert.True(WorktreePreviewService.TryParsePreviewHostLabel(label, out var workItemId, out var serviceName));
        Assert.Equal(id, workItemId);
        Assert.Equal(service, serviceName);
    }

    [Theory]
    [InlineData("wi-")]
    [InlineData("wi-abc")]
    [InlineData("wi-7-")]
    [InlineData("api")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_labels_are_not_preview_hostnames(string? label)
        => Assert.False(WorktreePreviewService.TryParsePreviewHostLabel(label, out _, out _));

    [Fact]
    public void A_named_service_resolves_to_its_own_port()
    {
        var rows = new[] { Row("app", isPublic: true, port: 5001), Row("api", port: 5002), Row("work-item-server", port: 5003) };

        var target = WorktreePreviewService.SelectPreviewTarget("7", "api", rows);

        Assert.Equal(PreviewTargetOutcome.Resolved, target.Outcome);
        Assert.Equal(5002, target.Port);
        Assert.Equal("api", target.ServiceName);
    }

    [Fact]
    public void A_service_name_matches_whatever_its_case_and_a_hyphenated_name_is_not_cut_short()
    {
        var rows = new[] { Row("app", isPublic: true), Row("work-item-server", port: 5003) };

        var target = WorktreePreviewService.SelectPreviewTarget("7", "WORK-ITEM-SERVER", rows);

        Assert.True(target.IsResolved);
        Assert.Equal(5003, target.Port);
        Assert.Equal("work-item-server", target.ServiceName);
    }

    [Fact]
    public void The_bare_label_resolves_to_the_one_public_service_and_rewrites_the_host_unless_it_opts_out()
    {
        var rewriting = WorktreePreviewService.SelectPreviewTarget("7", null, [Row("app", isPublic: true, port: 5001), Row("api", port: 5002)]);
        var optedOut = WorktreePreviewService.SelectPreviewTarget("7", null, [Row("app", isPublic: true, port: 5001, rewriteHost: false)]);

        Assert.True(rewriting.IsResolved);
        Assert.Equal(5001, rewriting.Port);
        Assert.Equal("app", rewriting.ServiceName);
        Assert.True(rewriting.RewriteHost);
        Assert.True(optedOut.IsResolved);
        Assert.False(optedOut.RewriteHost);
    }

    [Fact]
    public void A_named_service_that_is_stopped_or_unknown_is_reported_as_not_running()
    {
        var rows = new[] { Row("app", isPublic: true), Row("api", running: false) };

        var stopped = WorktreePreviewService.SelectPreviewTarget("7", "api", rows);
        var unknown = WorktreePreviewService.SelectPreviewTarget("7", "nope", rows);

        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, stopped.Outcome);
        Assert.Contains("api", stopped.Message);
        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, unknown.Outcome);
    }

    [Fact]
    public void The_bare_label_with_several_public_services_running_names_none_of_them()
    {
        var rows = new[] { Row("app", isPublic: true, port: 5001), Row("api", isPublic: true, port: 5002) };

        var ambiguous = WorktreePreviewService.SelectPreviewTarget("7", null, rows);

        // Serving whichever came first would put one application behind a hostname that describes the other just as well.
        Assert.Equal(PreviewTargetOutcome.AmbiguousService, ambiguous.Outcome);
        Assert.Contains("wi-7-app", ambiguous.Message);
        Assert.Contains("wi-7-api", ambiguous.Message);

        // Naming one is unambiguous and still works.
        var named = WorktreePreviewService.SelectPreviewTarget("7", "api", rows);
        Assert.True(named.IsResolved);
        Assert.Equal("api", named.ServiceName);
    }

    [Fact]
    public void Only_one_public_service_running_is_unambiguous_even_when_two_are_configured()
    {
        var rows = new[] { Row("app", isPublic: true, port: 5001), Row("api", isPublic: true, running: false, port: 5002) };

        var target = WorktreePreviewService.SelectPreviewTarget("7", null, rows);

        Assert.True(target.IsResolved);
        Assert.Equal("app", target.ServiceName);
        Assert.Equal(5001, target.Port);
    }

    [Fact]
    public void The_bare_label_with_no_public_service_running_is_reported_as_not_running()
    {
        var rows = new[] { Row("app", isPublic: true, running: false), Row("api") };

        var target = WorktreePreviewService.SelectPreviewTarget("7", null, rows);

        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, target.Outcome);
        Assert.Contains("public", target.Message);
    }

    [Fact]
    public void A_running_service_without_a_port_is_reported_as_not_running()
    {
        var target = WorktreePreviewService.SelectPreviewTarget("7", "api", [new("api", false, true, true, 0)]);

        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, target.Outcome);
    }

    [Fact]
    public async Task Unknown_work_item_is_reported_as_its_own_outcome()
    {
        var service = BuildService();

        var target = await service.ResolvePreviewTargetAsync("wi-404", WorkItemsReturning(null), TestContext.Current.CancellationToken);

        Assert.Equal(PreviewTargetOutcome.UnknownWorkItem, target.Outcome);
        Assert.Contains("404", target.Message);
    }

    [Fact]
    public async Task Work_item_without_a_worktree_is_reported_as_its_own_outcome()
    {
        var service = BuildService();
        var workItems = WorkItemsReturning(new WorkItemView { Id = "7", WorktreePath = null });

        var target = await service.ResolvePreviewTargetAsync("wi-7", workItems, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewTargetOutcome.NoWorktree, target.Outcome);
    }

    [Fact]
    public async Task Worktree_with_no_running_preview_is_reported_as_its_own_outcome()
    {
        WriteConfig();
        var service = BuildService();

        // Configured, never started: the runtime dictionary has no entry.
        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree(), TestContext.Current.CancellationToken);

        Assert.Equal(PreviewTargetOutcome.PreviewNotRunning, target.Outcome);
    }

    /// <summary>
    /// The proxy resolves a target on every HTTP request to a preview hostname —
    /// every asset, XHR and reload poll — while start/stop keeps changing the set of
    /// running processes. Enumerating a list that is being mutated throws
    /// "Collection was modified", which here would surface as a bare 500 on a page
    /// that happened to be loading when someone restarted a service.
    /// <para>
    /// So a reader that took the process list before a restart must be left holding
    /// exactly what it took. The list is held the way a resolve in flight holds it,
    /// then one service is stopped and started again under it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resolving_a_target_while_services_restart_does_not_tear()
    {
        WriteConfig();
        var service = BuildService();
        await service.StartAsync(_worktree, cancellationToken: TestContext.Current.CancellationToken);
        var workItems = WorkItemsPointingAtThisWorktree();

        var runtime = Assert.Single(service._runtimes.Values);
        var held = runtime.Processes;
        var before = held.ToArray();

        await service.StopServiceAsync(_worktree, "api", TestContext.Current.CancellationToken);
        await service.StartServiceAsync(_worktree, "api", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(before, held.ToArray());

        // Both forms: one filters the collection, the other scans it.
        Assert.Equal(PreviewTargetOutcome.Resolved, (await service.ResolvePreviewTargetAsync("wi-7", workItems, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(PreviewTargetOutcome.Resolved, (await service.ResolvePreviewTargetAsync("wi-7-api", workItems, TestContext.Current.CancellationToken)).Outcome);
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
