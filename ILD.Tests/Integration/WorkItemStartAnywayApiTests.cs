using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// POST /api/v1/workitems/{id}/start: a human starts a Ready item now, past its
/// repository's parallelism limit but not past the instance's concurrency cap.
/// The coordinator's claim rules are covered in <c>RepositoryParallelismTests</c>;
/// these pin what the route answers and that it shares the scheduler's claim gate.
/// </summary>
// A host per test: the work queue pass claims every ready item on the host and the engine mock records each start.
public class WorkItemStartAnywayApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host : IAsyncDisposable
    {
        public Mock<ILoopEngine> Engine { get; } = new();
        public ApiFactory Factory { get; }
        public HttpClient Human { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public Host()
        {
            var resolver = new Mock<ILoopTemplateResolver>();
            resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                    .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));
            Factory = new ApiFactory(configureServices: services =>
            {
                services.ReplaceSingleton(new Mock<IWorkItemNotifier>().Object);
                services.ReplaceSingleton(new Mock<IChatNotifier>().Object);
                services.ReplaceSingleton(Engine.Object);
                services.ReplaceSingleton(resolver.Object);
            });
        }

        public async Task<Host> StartAsync()
        {
            Human = await Factory.CreateAuthenticatedClientAsync();
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider
            {
                Id = Guid.NewGuid(), Name = "test-provider", Type = "forgejo", Url = "https://example.com", CreatedAt = DateTime.UtcNow,
            };
            var repo = new Repository
            {
                Id = Guid.NewGuid(), Name = "repo", CloneUrl = "https://example.com/repo.git", RemoteProviderId = provider.Id,
                DefaultIntakeStatus = WorkItemStatus.Backlog, Parallelism = 1, CreatedAt = DateTime.UtcNow,
            };
            db.RemoteProviders.Add(provider);
            db.Repositories.Add(repo);
            await db.SaveChangesAsync(Ct);
            RepositoryId = repo.Id;
            return this;
        }

        public async Task<string> CreateAsync(string title, string? status)
        {
            var created = await Human.PostAsJsonAsync("/api/v1/workitems", new
            {
                title,
                description = "",
                repositoryId = RepositoryId.ToString(),
            }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;
            if (status is not null)
            {
                var moved = await Human.PostAsJsonAsync($"/api/v1/workitems/{id}/transition", new { targetStatus = status }, Ct);
                Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            }
            return id;
        }

        public Task<HttpResponseMessage> StartAnywayAsync(string id)
            => Human.PostAsJsonAsync($"/api/v1/workitems/{id}/start", new { }, Ct);

        public async Task<string> StatusOfAsync(string id)
            => (await ReadJsonAsync(await Human.GetAsync($"/api/v1/workitems/{id}", Ct))).GetProperty("status").GetString()!;

        public async Task SetAsync(string key, string value)
        {
            var resp = await Human.PutAsJsonAsync($"/api/v1/settings/{key}", new { value }, Ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        public void SeedLiveRun(string workItemId)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t-" + workItemId };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            db.LoopTemplates.Add(template);
            db.LoopTemplateVersions.Add(version);
            db.LoopRuns.Add(new LoopRun
            {
                Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id,
                Status = LoopRunStatus.Running, StartedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task AssertRefusedAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await ReadJsonAsync(resp)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Start_anyway_starts_a_ready_item_even_while_paused_and_refuses_anything_not_ready()
    {
        await using var host = await new Host().StartAsync();

        var anonymous = await host.Factory.CreateClient().PostAsJsonAsync("/api/v1/workitems/any/start", new { }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.StartAnywayAsync("does-not-exist")).StatusCode);

        var backlog = await host.CreateAsync("still in backlog", status: null);
        await AssertRefusedAsync(await host.StartAnywayAsync(backlog));
        Assert.Equal("Backlog", await host.StatusOfAsync(backlog));

        await host.SetAsync(AppSettingKeys.SchedulerIsPaused, "true");
        var ready = await host.CreateAsync("start me", "Ready");

        var started = await host.StartAnywayAsync(ready);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var view = await ReadJsonAsync(started);
        Assert.Equal(ready, view.GetProperty("id").GetString());
        Assert.Equal("Running", view.GetProperty("status").GetString());
        Assert.Equal("Running", await host.StatusOfAsync(ready));
        host.Engine.Verify(e => e.StartRunAsync(ready, It.IsAny<CancellationToken>()), Times.Once);

        await AssertRefusedAsync(await host.StartAnywayAsync(ready));
        host.Engine.Verify(e => e.StartRunAsync(ready, It.IsAny<CancellationToken>()), Times.Once);
        host.Engine.Verify(e => e.StartRunAsync(backlog, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_anyway_is_refused_at_the_concurrency_cap_and_while_ild_is_shutting_down()
    {
        await using var host = await new Host().StartAsync();
        var ready = await host.CreateAsync("wait for room", "Ready");
        await host.SetAsync(AppSettingKeys.SchedulerMaxConcurrent, "1");
        host.SeedLiveRun("another-item");

        await AssertRefusedAsync(await host.StartAnywayAsync(ready));
        Assert.Equal("Ready", await host.StatusOfAsync(ready));

        await host.SetAsync(AppSettingKeys.SchedulerMaxConcurrent, "10");
        host.Factory.Services.GetRequiredService<IShutdownState>().SignalStopping();

        await AssertRefusedAsync(await host.StartAnywayAsync(ready));
        Assert.Equal("Ready", await host.StatusOfAsync(ready));
        host.Engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The scheduler resolves its coordinator in a scope of its own and a request
    /// in another, so the gate only serializes them if every scope shares it.
    /// </summary>
    [Fact]
    public async Task A_start_anyway_in_one_scope_waits_for_a_pass_in_another()
    {
        await using var host = await new Host().StartAsync();
        var claimedByPass = await host.CreateAsync("claimed by the pass", "Ready");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.Setup(e => e.StartRunAsync(claimedByPass, It.IsAny<CancellationToken>()))
                   .Returns(async () => { reached.TrySetResult(); await release.Task; });
        var opts = await host.Factory.Services.GetRequiredService<IWorkItemServerOptionsResolver>()
            .ResolveForRepositoryAsync(null, Ct);

        using var passScope = host.Factory.Services.CreateScope();
        var pass = passScope.ServiceProvider.GetRequiredService<IRemoteWorkItemCoordinator>()
            .RunPollCycleAsync(opts, maxConcurrent: 10, claimReadyItems: true, Ct);
        await reached.Task;
        var byHand = await host.CreateAsync("by hand", "Ready");

        using var startScope = host.Factory.Services.CreateScope();
        var start = startScope.ServiceProvider.GetRequiredService<IRemoteWorkItemCoordinator>()
            .StartIgnoringRepositoryLimitAsync(opts, byHand, 10, WorkItemActor.Human(host.Factory.AdminUsername), Ct);
        Assert.False(start.IsCompleted);

        release.SetResult();
        Assert.Contains(claimedByPass, (await pass).Claimed.Select(w => w.Id));
        Assert.Equal(HandStartResult.Started, await start);
    }
}
