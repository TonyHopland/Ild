using ILD.Core.Services.RunHosts;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The work-item-addressed preview both the UI and the agent routes go through (ADR-0011): worktree resolution,
/// start options, the board broadcast and the classification of the preview's refusals.
/// </summary>
public class WorkItemPreviewTests
{
    private const string WorktreePath = "/tmp/worktrees/work-item-preview";

    private sealed class Harness : IDisposable
    {
        public TestDb Db { get; } = new();
        public Mock<IWorktreePreviewService> Preview { get; } = new();
        public Mock<IWorkItemNotifier> Notifier { get; } = new();
        public WorkItemPreview Module { get; }
        public string Id { get; } = Guid.NewGuid().ToString();

        public Harness(WorkItemView? item = null, bool known = true)
        {
            var workItems = new Mock<IWorkItemManager>();
            var view = item ?? new WorkItemView { WorktreePath = WorktreePath };
            view.Id = Id;
            workItems.Setup(m => m.GetWorkItemAsync(Id)).ReturnsAsync(known ? view : null);
            Module = new WorkItemPreview(workItems.Object, Preview.Object, Db.Providers, NoPackageFeeds.Resolver, Notifier.Object);
        }

        public void Dispose() => Db.Dispose();

        public void NoBroadcast() => Notifier.Verify(n => n.PreviewStateChangedAsync(It.IsAny<string>()), Times.Never);
    }

    private static WorktreePreviewResponse Answer(string state = "running") => new() { State = state, WorktreePath = WorktreePath };

    private static void AssertRefused<T>(PreviewResult<T> result, PreviewRefusal refusal, string? message = null)
    {
        Assert.Equal(refusal, result.Refusal);
        if (message != null) Assert.Equal(message, result.Message);
    }

    /// <summary>Every route, called with valid arguments, for the cases that hold for all of them.</summary>
    public static IEnumerable<object[]> Routes()
    {
        yield return Route("status", (m, id) => m.GetStatusAsync(id));
        yield return Route("start", (m, id) => m.StartAsync(id, new WorktreePreviewStartRequest()));
        yield return Route("stop", (m, id) => m.StopAsync(id));
        yield return Route("start-service", (m, id) => m.StartServiceAsync(id, "web", new WorktreePreviewStartRequest(), false));
        yield return Route("stop-service", (m, id) => m.StopServiceAsync(id, "web"));
        yield return Route("get-config", (m, id) => m.GetServiceConfigAsync(id, "web"));
        yield return Route("update-config", (m, id) => m.UpdateServiceConfigAsync(id, "web", new WorktreePreviewServiceConfigUpdateRequest { Config = "{}" }));
        yield return Route("log", (m, id) => m.GetLogAsync(id, "web"));
    }

    private static object[] Route<T>(string name, Func<IWorkItemPreview, string, Task<PreviewResult<T>>> call)
        => [name, async (IWorkItemPreview m, string id) =>
        {
            var result = await call(m, id);
            return result.Refusal is { } kind ? new Refusal(kind, result.Message) : null;
        }];

    public sealed record Refusal(PreviewRefusal Kind, string? Message);

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_unknown_work_item_is_not_found_and_nothing_is_called(string _, Func<IWorkItemPreview, string, Task<Refusal?>> call)
    {
        using var h = new Harness(known: false);

        var refusal = await call(h.Module, h.Id);

        Assert.Equal(PreviewRefusal.WorkItemNotFound, refusal?.Kind);
        Assert.Empty(h.Preview.Invocations);
        h.NoBroadcast();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_work_item_without_a_worktree_is_refused_and_nothing_is_called(string _, Func<IWorkItemPreview, string, Task<Refusal?>> call)
    {
        using var h = new Harness(new WorkItemView { WorktreePath = null });

        var refusal = await call(h.Module, h.Id);

        Assert.Equal(new Refusal(PreviewRefusal.NoWorktree, "Work item does not currently have an active worktree."), refusal);
        Assert.Empty(h.Preview.Invocations);
        h.NoBroadcast();
    }

    [Fact]
    public async Task A_lost_run_pod_behind_the_preview_is_a_pod_lost_refusal_with_the_lost_text_not_a_plain_refusal()
    {
        using var h = new Harness();
        var lost = new RunPodLostException(Guid.NewGuid(), RunTermination.LostText("pod"));
        h.Preview.Setup(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("could not run", lost));

        var result = await h.Module.GetStatusAsync(h.Id);

        AssertRefused(result, PreviewRefusal.PodLost, lost.Message);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result.ToActionResult());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_InvalidOperationException_from_the_preview_is_a_refusal_with_its_message(string _, Func<IWorkItemPreview, string, Task<Refusal?>> call)
    {
        using var h = new Harness();
        var boom = new InvalidOperationException("preview says no");
        h.Preview.Setup(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.StartServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.StopServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.GetServiceConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.UpdateServiceConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        h.Preview.Setup(p => p.GetServiceLogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);

        var refusal = await call(h.Module, h.Id);

        Assert.Equal(new Refusal(PreviewRefusal.Refused, "preview says no"), refusal);
        h.NoBroadcast();
    }

    [Fact]
    public async Task Mutating_routes_broadcast_once_and_reads_stay_silent()
    {
        using var h = new Harness();
        h.Preview.Setup(p => p.GetStatusAsync(WorktreePath, It.IsAny<CancellationToken>())).ReturnsAsync(Answer());
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer());
        h.Preview.Setup(p => p.StopAsync(WorktreePath, It.IsAny<CancellationToken>())).ReturnsAsync(Answer("stopped"));
        h.Preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer());
        h.Preview.Setup(p => p.StopServiceAsync(WorktreePath, "web", It.IsAny<CancellationToken>())).ReturnsAsync(Answer("stopped"));
        h.Preview.Setup(p => p.GetServiceConfigAsync(WorktreePath, "web", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");
        h.Preview.Setup(p => p.GetServiceLogAsync(WorktreePath, "web", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("log");

        await h.Module.GetStatusAsync(h.Id);
        await h.Module.GetServiceConfigAsync(h.Id, "web");
        await h.Module.UpdateServiceConfigAsync(h.Id, "web", new WorktreePreviewServiceConfigUpdateRequest { Config = "{}" });
        await h.Module.GetLogAsync(h.Id, "web");
        h.NoBroadcast();

        await h.Module.StartAsync(h.Id, null);
        await h.Module.StopAsync(h.Id);
        await h.Module.StartServiceAsync(h.Id, "web", null, false);
        await h.Module.StopServiceAsync(h.Id, "web");
        h.Notifier.Verify(n => n.PreviewStateChangedAsync(h.Id), Times.Exactly(4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_an_explicit_wait_false_begins_the_start_in_the_background(bool? wait)
    {
        using var h = new Harness();
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer());
        h.Preview.Setup(p => p.BeginStartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer("starting"));
        var request = wait is { } w ? new WorktreePreviewStartRequest { Wait = w } : null;

        var result = await h.Module.StartAsync(h.Id, request);

        Assert.Null(result.Refusal);
        var background = wait == false;
        h.Preview.Verify(p => p.BeginStartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), background ? Times.Once : Times.Never);
        h.Preview.Verify(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), background ? Times.Never : Times.Once);
        h.Notifier.Verify(n => n.PreviewStateChangedAsync(h.Id), Times.Once);
    }

    [Fact]
    public async Task The_request_reaches_the_start_options()
    {
        using var h = new Harness();
        WorktreePreviewStartOptions? captured = null;
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(Answer());

        await h.Module.StartAsync(h.Id, new WorktreePreviewStartRequest { ProfileName = "app", SkipInstall = true, PublicHost = "h" });

        Assert.Equal("app", captured!.ProfileName);
        Assert.True(captured.SkipInstall);
        Assert.Equal("h", captured.PublicHost);
        Assert.Equal(h.Id, captured.WorkItemId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_service_start_passes_start_dependencies_through_as_given(bool startDependencies)
    {
        using var h = new Harness();
        WorktreePreviewStartOptions? captured = null;
        h.Preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorktreePreviewStartOptions?, CancellationToken>((_, _, o, _) => captured = o)
            .ReturnsAsync(Answer());

        // The request's own flag is ignored: the caller decides, and the agent route says false.
        await h.Module.StartServiceAsync(h.Id, "web", new WorktreePreviewStartRequest { StartDependencies = !startDependencies }, startDependencies);

        Assert.Equal(startDependencies, captured!.StartDependencies);
    }

    [Fact]
    public async Task A_whole_preview_start_does_not_start_dependencies_of_a_service()
    {
        using var h = new Harness();
        WorktreePreviewStartOptions? captured = null;
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(Answer());

        await h.Module.StartAsync(h.Id, new WorktreePreviewStartRequest { StartDependencies = true });

        Assert.False(captured!.StartDependencies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_service_is_refused_before_the_work_item_is_looked_up(string service)
    {
        using var h = new Harness(known: false);

        AssertRefused(await h.Module.StartServiceAsync(h.Id, service, null, false), PreviewRefusal.Refused, "service is required.");
        AssertRefused(await h.Module.StopServiceAsync(h.Id, service), PreviewRefusal.Refused, "service is required.");
        AssertRefused(await h.Module.GetServiceConfigAsync(h.Id, service), PreviewRefusal.Refused, "service is required.");
        AssertRefused(await h.Module.GetLogAsync(h.Id, service), PreviewRefusal.Refused, "service is required.");
        AssertRefused(await h.Module.UpdateServiceConfigAsync(h.Id, service, new() { Config = "{}" }), PreviewRefusal.Refused, "service is required.");
        Assert.Empty(h.Preview.Invocations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task A_config_update_needs_a_config_before_the_work_item_is_looked_up(string? config)
    {
        using var h = new Harness(known: false);
        var request = config == null ? null : new WorktreePreviewServiceConfigUpdateRequest { Config = config };

        AssertRefused(await h.Module.UpdateServiceConfigAsync(h.Id, "web", request), PreviewRefusal.Refused, "config is required.");
        Assert.Empty(h.Preview.Invocations);
    }

    [Fact]
    public async Task A_service_without_a_config_is_a_config_not_found_refusal()
    {
        using var h = new Harness();
        h.Preview.Setup(p => p.GetServiceConfigAsync(WorktreePath, "web", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        AssertRefused(await h.Module.GetServiceConfigAsync(h.Id, "web"), PreviewRefusal.ConfigNotFound, "No preview config found for service 'web'.");
    }

    [Fact]
    public async Task A_config_update_answers_with_the_config_as_stored()
    {
        using var h = new Harness();
        h.Preview.Setup(p => p.GetServiceConfigAsync(WorktreePath, "web", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("{\"a\":1}");

        var result = await h.Module.UpdateServiceConfigAsync(h.Id, "web", new() { Config = "{\"a\":1}" });

        Assert.Equal("web", result.Value!.Service);
        Assert.Equal("{\"a\":1}", result.Value.Config);
        h.Preview.Verify(p => p.UpdateServiceConfigAsync(WorktreePath, "web", "{\"a\":1}", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Results_map_to_the_same_statuses_and_bodies_on_every_surface()
    {
        Assert.Equal(200, Assert.IsType<OkObjectResult>(PreviewResult<string>.Ok("x").ToActionResult()).StatusCode);
        var missing = PreviewResult<string>.Failed(PreviewRefusal.WorkItemNotFound).ToActionResult();
        Assert.IsType<NotFoundResult>(missing);
        var noConfig = Assert.IsType<NotFoundObjectResult>(PreviewResult<string>.Failed(PreviewRefusal.ConfigNotFound, "m").ToActionResult());
        Assert.Equal("m", noConfig.Value!.GetType().GetProperty("error")!.GetValue(noConfig.Value));
        foreach (var refusal in new[] { PreviewRefusal.NoWorktree, PreviewRefusal.Refused })
        {
            var bad = Assert.IsType<BadRequestObjectResult>(PreviewResult<string>.Failed(refusal, "m").ToActionResult());
            Assert.Equal("m", bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_start_takes_the_repository_custom_env_of_the_run_not_of_the_item(bool itemRepointed)
    {
        using var h = new Harness();
        var (runRepo, editedRepo) = await SeedRepositoriesAsync(h.Db, keepData: true);
        var item = itemRepointed
            ? new WorkItemView { WorktreePath = WorktreePath, RepositoryId = editedRepo, RunRepositoryId = runRepo }
            : new WorkItemView { WorktreePath = WorktreePath, RepositoryId = runRepo, RunRepositoryId = runRepo };
        var module = ModuleOver(h, item);
        var captured = new List<WorktreePreviewStartOptions?>();
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured.Add(o))
            .ReturnsAsync(Answer());
        h.Preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorktreePreviewStartOptions?, CancellationToken>((_, _, o, _) => captured.Add(o))
            .ReturnsAsync(Answer());

        await module.StartAsync(h.Id, new WorktreePreviewStartRequest());
        await module.StartServiceAsync(h.Id, "web", new WorktreePreviewStartRequest(), false);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, o => Assert.Equal("API_TOKEN=from-run-repo", o!.CustomEnv));
    }

    [Fact]
    public async Task A_start_carries_the_volume_scope_of_the_runs_repository_or_none_without_one()
    {
        using var h = new Harness();
        var (runRepo, editedRepo) = await SeedRepositoriesAsync(h.Db, keepData: true);
        var captured = new List<WorktreePreviewStartOptions?>();
        h.Preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured.Add(o))
            .ReturnsAsync(Answer());
        h.Preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorktreePreviewStartOptions?, CancellationToken>((_, _, o, _) => captured.Add(o))
            .ReturnsAsync(Answer());
        var withRepository = ModuleOver(h, new WorkItemView { Title = "A title", WorktreePath = WorktreePath, RepositoryId = editedRepo, RunRepositoryId = runRepo });
        var withoutRepository = ModuleOver(h, new WorkItemView { Title = "A title", WorktreePath = WorktreePath });

        await withRepository.StartAsync(h.Id, new WorktreePreviewStartRequest());
        await withRepository.StartServiceAsync(h.Id, "web", new WorktreePreviewStartRequest(), false);
        await withoutRepository.StartAsync(h.Id, new WorktreePreviewStartRequest());

        var expected = new PreviewVolumeScope(runRepo, true, h.Id, "A title");
        Assert.Equal(3, captured.Count);
        Assert.Equal(expected, captured[0]!.Volumes);
        Assert.Equal(expected, captured[1]!.Volumes);
        Assert.Null(captured[2]!.Volumes);
    }

    private static WorkItemPreview ModuleOver(Harness h, WorkItemView item)
    {
        var workItems = new Mock<IWorkItemManager>();
        item.Id = h.Id;
        workItems.Setup(m => m.GetWorkItemAsync(h.Id)).ReturnsAsync(item);
        return new WorkItemPreview(workItems.Object, h.Preview.Object, h.Db.Providers, NoPackageFeeds.Resolver, h.Notifier.Object);
    }

    private static async Task<(Guid Run, Guid Edited)> SeedRepositoriesAsync(TestDb db, bool keepData)
    {
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "https://e" };
        db.Context.RemoteProviders.Add(provider);
        var run = Guid.NewGuid();
        var edited = Guid.NewGuid();
        db.Context.Repositories.Add(new Repository
        {
            Id = run, Name = "r", CloneUrl = "https://e/r.git", RemoteProviderId = provider.Id,
            PreviewEnv = "API_TOKEN=from-run-repo", KeepPreviewData = keepData,
        });
        // Re-pointing the item since must not reach the run it already has.
        db.Context.Repositories.Add(new Repository
        {
            Id = edited, Name = "b", CloneUrl = "https://e/b.git", RemoteProviderId = provider.Id,
            PreviewEnv = "API_TOKEN=from-edited-repo", KeepPreviewData = !keepData,
        });
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (run, edited);
    }
}
