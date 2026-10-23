using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

internal static class FinishedRunCleanerWiring
{
    /// <summary>
    /// The real <see cref="FinishedRunCleaner"/>, a singleton built by the container as the
    /// API host builds it. It finds its stores, reclaimer and engine in the scopes it opens.
    /// </summary>
    public static IServiceCollection AddRealFinishedRunCleaner(this IServiceCollection services)
    {
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));
        services.AddSingleton<IFinishedRunCleaner>(sp => ActivatorUtilities.CreateInstance<FinishedRunCleaner>(sp));
        return services;
    }

    /// <summary>Per-scope contexts on the test database, as the API host registers them.</summary>
    public static IServiceCollection AddScopedRunStores(this IServiceCollection services, TestDb db)
    {
        services.AddScoped(_ => db.Fresh());
        services.AddScoped<ILoopRunStore>(sp => new LoopRunStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IAdapterSessionSnapshotStore>(sp => new AdapterSessionSnapshotStore(sp.GetRequiredService<AppDbContext>()));
        return services;
    }

    public static async Task SeedSnapshotAsync(TestDb db, Guid runId, string sessionId = "sess-1")
    {
        await using var ctx = db.Fresh();
        await SessionSnapshotTestSupport.SeedAsync(new AdapterSessionSnapshotStore(ctx),
            SessionSnapshotKey.ForRun(runId, "ClaudeCode", sessionId), "claude-jsonl", "{\"turn\":1}\n", CancellationToken.None);
    }

    public static int SnapshotCount(TestDb db, Guid runId)
    {
        using var ctx = db.Fresh();
        return ctx.AdapterSessionSnapshots.Count(s => s.LoopRunId == runId);
    }

    public static int ChunkCount(TestDb db, Guid runId)
    {
        using var ctx = db.Fresh();
        var ids = ctx.AdapterSessionSnapshots.Where(s => s.LoopRunId == runId).Select(s => s.Id).ToList();
        return ctx.AdapterSessionSnapshotChunks.Count(c => ids.Contains(c.SnapshotId));
    }
}

/// <summary>
/// End-of-run removal: once a run has ended and no loop drives it, its worktree, local
/// branch and AI session snapshots go and the run row stays. A pinned run, a run still
/// in progress and a run the engine still owns are never touched.
/// </summary>
public sealed class FinishedRunCleanerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IRunReclaimer> _reclaimer = new();
    private readonly HashSet<Guid> _owned = [];
    private readonly IFinishedRunCleaner _cleaner;
    private readonly Guid _versionId;

    public FinishedRunCleanerTests()
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.SaveChanges();
        _versionId = version.Id;

        _reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(() => _owned.ToArray());

        var services = new ServiceCollection();
        services.AddScopedRunStores(_db);
        services.AddSingleton(_reclaimer.Object);
        services.AddSingleton(engine.Object);
        services.AddRealFinishedRunCleaner();
        _cleaner = services.BuildServiceProvider().GetRequiredService<IFinishedRunCleaner>();
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(LoopRunStatus.Completed, false)]
    [InlineData(LoopRunStatus.Cancelled, false)]
    [InlineData(LoopRunStatus.Completed, true)]
    // Failed runs are left to the sweeper, which asks for them once their item is Done or gone.
    [InlineData(LoopRunStatus.Failed, true)]
    public async Task An_ended_unpinned_run_loses_its_local_state_and_keeps_its_row(LoopRunStatus status, bool allowFailed)
    {
        var runId = await SeedRunAsync(status);

        Assert.True(await _cleaner.RemoveLocalStateAsync(runId, allowFailed));

        _reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == runId)), Times.Once);
        var run = Read(runId);
        Assert.NotNull(run);
        Assert.Equal(status, run!.Status);
        Assert.Null(run.WorktreePath);
        Assert.Null(run.BranchName);
        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(_db, runId));
        Assert.Equal(0, FinishedRunCleanerWiring.ChunkCount(_db, runId));
    }

    [Theory]
    [InlineData(LoopRunStatus.Running, false, false, true)]
    [InlineData(LoopRunStatus.WaitingHuman, false, false, true)]
    [InlineData(LoopRunStatus.Failed, false, false, false)]
    [InlineData(LoopRunStatus.Completed, true, false, true)]
    [InlineData(LoopRunStatus.Cancelled, true, false, false)]
    [InlineData(LoopRunStatus.Completed, false, true, false)]
    [InlineData(LoopRunStatus.Cancelled, false, true, true)]
    public async Task A_run_in_progress_failed_pinned_or_still_driven_by_the_engine_is_left_untouched(
        LoopRunStatus status, bool pinned, bool engineOwned, bool allowFailed)
    {
        var runId = await SeedRunAsync(status, retain: pinned);
        if (engineOwned) _owned.Add(runId);

        Assert.False(await _cleaner.RemoveLocalStateAsync(runId, allowFailed));

        _reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        AssertUntouched(runId);
    }

    [Fact]
    public async Task An_unknown_run_is_a_no_op()
    {
        Assert.False(await _cleaner.RemoveLocalStateAsync(Guid.NewGuid()));
        _reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
    }

    [Fact]
    public async Task Snapshots_left_behind_without_a_worktree_are_still_deleted()
    {
        var runId = await SeedRunAsync(LoopRunStatus.Completed, withGitState: false);

        await _cleaner.RemoveLocalStateAsync(runId);

        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(_db, runId));
        Assert.NotNull(Read(runId));
    }

    [Fact]
    public async Task A_failed_removal_keeps_the_pointers_for_the_next_attempt_and_never_throws()
    {
        var runId = await SeedRunAsync(LoopRunStatus.Completed);
        _reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(false);

        await _cleaner.RemoveLocalStateAsync(runId);

        var run = Read(runId)!;
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Equal($"/tmp/wt/{runId}", run.WorktreePath);
        Assert.Equal($"ild/wi-x-run-{runId}", run.BranchName);

        _reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ThrowsAsync(new IOException("Device or resource busy"));
        await _cleaner.RemoveLocalStateAsync(runId);
        Assert.Equal($"/tmp/wt/{runId}", Read(runId)!.WorktreePath);

        _reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
        Assert.True(await _cleaner.RemoveLocalStateAsync(runId));
        Assert.Null(Read(runId)!.WorktreePath);
        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(_db, runId));
    }

    [Fact]
    public async Task A_second_removal_while_the_first_is_in_flight_does_nothing()
    {
        var runId = await SeedRunAsync(LoopRunStatus.Completed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).Returns(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });

        var first = _cleaner.RemoveLocalStateAsync(runId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(await _cleaner.RemoveLocalStateAsync(runId));

        release.SetResult(true);
        Assert.True(await first);
        _reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Once);
        Assert.Null(Read(runId)!.WorktreePath);
    }

    private async Task<Guid> SeedRunAsync(LoopRunStatus status, bool retain = false, bool withGitState = true)
    {
        var id = Guid.NewGuid();
        await using (var ctx = _db.Fresh())
        {
            ctx.LoopRuns.Add(new LoopRun
            {
                Id = id,
                WorkItemId = $"wi-{id:N}",
                LoopTemplateVersionId = _versionId,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = status,
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman ? null : DateTime.UtcNow,
                WorktreePath = withGitState ? $"/tmp/wt/{id}" : null,
                BranchName = withGitState ? $"ild/wi-x-run-{id}" : null,
                Retain = retain,
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await FinishedRunCleanerWiring.SeedSnapshotAsync(_db, id, "sess-1");
        await FinishedRunCleanerWiring.SeedSnapshotAsync(_db, id, "sess-2");
        return id;
    }

    private LoopRun? Read(Guid runId)
    {
        using var ctx = _db.Fresh();
        return ctx.LoopRuns.AsNoTracking().FirstOrDefault(r => r.Id == runId);
    }

    private void AssertUntouched(Guid runId)
    {
        var run = Read(runId)!;
        Assert.Equal($"/tmp/wt/{runId}", run.WorktreePath);
        Assert.Equal($"ild/wi-x-run-{runId}", run.BranchName);
        Assert.Equal(2, FinishedRunCleanerWiring.SnapshotCount(_db, runId));
    }
}
