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
[Collection("EnvironmentPath")]
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
            NullLogger<WorktreePreviewService>.Instance);
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
    public async Task Bare_work_item_label_resolves_to_the_public_services_port()
    {
        WriteConfig();
        var service = BuildService();
        var started = await service.StartAsync(_worktree);
        var appPort = started.Services.Single(s => s.Name == "app").Port;

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        Assert.Equal(PreviewTargetOutcome.Resolved, target.Outcome);
        Assert.True(target.IsResolved);
        Assert.Equal(appPort, target.Port);
        Assert.Equal("app", target.ServiceName);
        // Host rewriting is on unless a service opts out, because host-checking dev
        // servers reject the preview hostname outright.
        Assert.True(target.RewriteHost);
    }

    [Fact]
    public async Task Named_service_label_resolves_to_that_services_port()
    {
        WriteConfig();
        var service = BuildService();
        var started = await service.StartAsync(_worktree);
        var apiPort = started.Services.Single(s => s.Name == "api").Port;

        var target = await service.ResolvePreviewTargetAsync("wi-7-api", WorkItemsPointingAtThisWorktree());

        Assert.Equal(PreviewTargetOutcome.Resolved, target.Outcome);
        Assert.Equal(apiPort, target.Port);
        Assert.Equal("api", target.ServiceName);
    }

    [Fact]
    public async Task Hyphenated_service_name_is_not_mistaken_for_part_of_the_work_item_id()
    {
        WriteConfig();
        var service = BuildService();
        var started = await service.StartAsync(_worktree);
        var expected = started.Services.Single(s => s.Name == "work-item-server").Port;

        var target = await service.ResolvePreviewTargetAsync("wi-7-work-item-server", WorkItemsPointingAtThisWorktree());

        Assert.Equal(PreviewTargetOutcome.Resolved, target.Outcome);
        Assert.Equal(expected, target.Port);
        Assert.Equal("work-item-server", target.ServiceName);
    }

    [Fact]
    public async Task RewriteHost_false_travels_from_the_service_config_to_the_target()
    {
        WriteConfig(rewriteHostOnApp: false);
        var service = BuildService();
        await service.StartAsync(_worktree);

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        Assert.True(target.IsResolved);
        Assert.False(target.RewriteHost);
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

        var target = await service.ResolvePreviewTargetAsync(label, workItems.Object);

        Assert.Equal(PreviewTargetOutcome.NotAPreviewHost, target.Outcome);
        Assert.False(target.IsResolved);
        // Strict mock: rejecting the label must not have cost a work-item lookup.
        workItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unknown_work_item_is_reported_as_its_own_outcome()
    {
        var service = BuildService();

        var target = await service.ResolvePreviewTargetAsync("wi-404", WorkItemsReturning(null));

        Assert.Equal(PreviewTargetOutcome.UnknownWorkItem, target.Outcome);
        Assert.Contains("404", target.Message);
    }

    [Fact]
    public async Task Work_item_without_a_worktree_is_reported_as_its_own_outcome()
    {
        var service = BuildService();
        var workItems = WorkItemsReturning(new WorkItemView { Id = "7", WorktreePath = null });

        var target = await service.ResolvePreviewTargetAsync("wi-7", workItems);

        Assert.Equal(PreviewTargetOutcome.NoWorktree, target.Outcome);
    }

    [Fact]
    public async Task Worktree_with_no_running_preview_is_reported_as_its_own_outcome()
    {
        WriteConfig();
        var service = BuildService();

        // Configured, never started: the runtime dictionary has no entry.
        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        Assert.Equal(PreviewTargetOutcome.PreviewNotRunning, target.Outcome);
    }

    [Fact]
    public async Task Named_service_that_is_not_running_is_reported_as_its_own_outcome()
    {
        WriteConfig();
        var service = BuildService();
        await service.StartServiceAsync(_worktree, "app");

        // The preview is up, but only 'app' is; 'api' is configured and stopped.
        var stopped = await service.ResolvePreviewTargetAsync("wi-7-api", WorkItemsPointingAtThisWorktree());
        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, stopped.Outcome);
        Assert.Contains("api", stopped.Message);

        // ...as is a name that does not exist in the profile at all.
        var unknown = await service.ResolvePreviewTargetAsync("wi-7-nope", WorkItemsPointingAtThisWorktree());
        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, unknown.Outcome);
    }

    [Fact]
    public async Task Bare_label_with_several_public_services_running_names_none_of_them()
    {
        WriteConfig(alsoPublic: "api");
        var service = BuildService();
        await service.StartAsync(_worktree);

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        // Serving whichever came first would put one application behind a hostname
        // that describes the other just as well.
        Assert.Equal(PreviewTargetOutcome.AmbiguousService, target.Outcome);
        Assert.Contains("wi-7-app", target.Message);
        Assert.Contains("wi-7-api", target.Message);

        // Naming one is unambiguous and still works.
        var named = await service.ResolvePreviewTargetAsync("wi-7-api", WorkItemsPointingAtThisWorktree());
        Assert.True(named.IsResolved);
        Assert.Equal("api", named.ServiceName);
    }

    [Fact]
    public async Task Only_one_public_service_running_is_unambiguous_even_when_two_are_configured()
    {
        WriteConfig(alsoPublic: "api");
        var service = BuildService();
        var started = await service.StartServiceAsync(_worktree, "app");

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        Assert.True(target.IsResolved);
        Assert.Equal("app", target.ServiceName);
        Assert.Equal(started.Services.Single(s => s.Name == "app").Port, target.Port);
    }

    [Fact]
    public async Task Bare_label_with_no_public_service_running_is_reported_as_its_own_outcome()
    {
        WriteConfig();
        var service = BuildService();
        await service.StartServiceAsync(_worktree, "api");

        var target = await service.ResolvePreviewTargetAsync("wi-7", WorkItemsPointingAtThisWorktree());

        Assert.Equal(PreviewTargetOutcome.ServiceNotRunning, target.Outcome);
        Assert.Contains("public", target.Message);
    }

    /// <summary>
    /// The proxy resolves a target on every HTTP request to a preview hostname —
    /// every asset, XHR and reload poll — while start/stop keeps changing the set of
    /// running processes. Enumerating a list that is being mutated throws
    /// "Collection was modified", which here would surface as a bare 500 on a page
    /// that happened to be loading when someone restarted a service.
    /// <para>
    /// A stress test can only ever fail to reproduce a race, never falsely report
    /// one, so this is a guard rather than a proof: it fails reliably against an
    /// in-place mutated list and cannot fail against the copy-on-write one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resolving_a_target_while_services_restart_does_not_tear()
    {
        WriteConfig();
        var service = BuildService();
        await service.StartAsync(_worktree);
        var workItems = WorkItemsPointingAtThisWorktree();

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var resolutions = 0;

        var reader = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                // Both forms: one filters the collection, the other scans it.
                await service.ResolvePreviewTargetAsync("wi-7", workItems);
                await service.ResolvePreviewTargetAsync("wi-7-api", workItems);
                Interlocked.Increment(ref resolutions);
            }
        });

        for (var i = 0; i < 6 && !done.IsCancellationRequested; i++)
        {
            await service.StopServiceAsync(_worktree, "api");
            await service.StartServiceAsync(_worktree, "api");
        }

        done.Cancel();
        await reader; // An unhandled enumeration failure surfaces here.

        Assert.True(resolutions > 0, "the reader should have resolved at least once");
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
