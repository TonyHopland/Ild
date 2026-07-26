using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// <c>StopRunAsync</c> is how the control plane ends a run — the cancel button,
/// a human sending the work item to Done, a poll pass reacting to a Done the
/// server already holds, startup reconcile. The engine's own lifecycle ends its
/// runs directly instead (Completed at a Cleanup node, Failed on a node failure
/// or crash), and needs no help doing so: a work item leaves the Active Work
/// Item Set as soon as its run's row leaves <c>LoopRunStore.IsAlive</c>,
/// whichever writer got there first.
///
/// What this one owns is the run and only the run. Deciding what the work item
/// should say next belongs to whoever asked, because they all want something
/// different — Done for a human finishing it, HumanFeedback for the cancel
/// button, and nothing at all for a poll pass reacting to a status the server
/// already has.
/// </summary>
public class LoopEngineStopRunTests
{
    [Fact]
    public async Task Stopping_a_run_ends_it_and_leaves_the_work_item_alone()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a");

        await h.Engine.StopRunAsync(run.Id, "Work item marked Done");

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Cancelled, after.Status);
        Assert.NotNull(after.CompletedAt);
        Assert.Equal("Work item marked Done", after.HumanFeedbackReason);

        // The whole point: no disposition of its own. A caller on its way to
        // Done would otherwise have to overwrite one, and the overwritten state
        // still leaves a conversation entry and a notification behind.
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Cancelling_a_run_ends_it_and_parks_the_work_item_for_a_human()
    {
        // The cancel button's meaning, and the only caller that wants the park.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a");

        await h.Engine.CancelRunAsync(run.Id);

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Stopping_an_unknown_run_is_a_no_op()
    {
        // Two passes can both see the same finished item before either writes.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.SeedRun("a");

        Assert.Null(await h.Engine.StopRunAsync(Guid.NewGuid(), "gone"));
        Assert.Equal(LoopRunStatus.Running, h.ReloadRun().Status);
    }

    [Fact]
    public async Task Stopping_a_run_that_already_ended_leaves_it_as_it_ended()
    {
        // Callers read a run in one scope and stop it in another, so the engine
        // can finish it in between. Relabelling a Completed run Cancelled would
        // lose how it actually turned out.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a", LoopRunStatus.Completed);

        var workItemId = await h.Engine.StopRunAsync(run.Id, "too late");

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        // Still reported: the slot is free either way, which is what the caller
        // asked about.
        Assert.Equal(h.WorkItemId, workItemId);
    }

    /// <summary>
    /// The seam the split-scope regression hid in, so it is driven the way
    /// production wires it: an <c>AppDbContext</c> per scope and a real
    /// <see cref="WorkItemManager"/>. With a shared context (as the shared
    /// harness registers) nothing can go stale across scopes, and with a mocked
    /// manager the write-back never happens — so neither can see it.
    ///
    /// What it catches: <c>CancelRunAsync</c> loading the run in its own scope
    /// before delegating the write to <c>StopRunAsync</c>'s scope. The
    /// HumanFeedback transition then re-resolves the run through the first
    /// scope, EF identity resolution hands back the pre-cancel instance, and
    /// <c>UpdateRunAsync</c> writes every column — restoring Running over the
    /// cancel. The run then has no driving task, so the watchdog recovers it
    /// per its RecoveryPolicy, its worktree is never reclaimed, and its work
    /// item holds a concurrency slot forever: WI-165's own leak, through the
    /// cancel door.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_run_leaves_it_cancelled_when_every_scope_has_its_own_context()
    {
        using var db = new TestDb();

        // A real work item on the fake server: without one TransitionAsync
        // returns before it ever reaches the run, and the write-back this test
        // exists to catch never happens.
        var seedMgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object, db.Providers, new Mock<IEventLogService>().Object,
            db.LoopRuns, db.ServerClient, db.ServerOptions);
        var workItemId = await seedMgr.CreateWorkItemAsync("cancel me", "", null);

        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        var services = new ServiceCollection();
        // One context per scope, as the API host registers it — this is what
        // makes an entity tracked in one scope stale in the next.
        services.AddScoped(_ => db.Fresh());
        services.AddScoped<ILoopRunStore>(sp => new LoopRunStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<ILoopTemplateStore>(sp => new LoopTemplateStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IWorkItemManager>(sp => new WorkItemManager(
            new Mock<IRepositoryManager>().Object, db.Providers, new Mock<IEventLogService>().Object,
            sp.GetRequiredService<ILoopRunStore>(), db.ServerClient, db.ServerOptions));
        services.AddSingleton<IRunNotifier>(new NoopRunNotifier());
        services.AddSingleton<IWorkItemNotifier>(new Mock<IWorkItemNotifier>().Object);
        using var sp = services.BuildServiceProvider();

        var engine = new LoopEngine(sp, new ScriptedExecutorRegistry(),
            sp.GetRequiredService<IRunNotifier>(), NullLogger<LoopEngine>.Instance,
            sp.GetRequiredService<IWorkItemNotifier>());

        await engine.CancelRunAsync(run.Id);

        using var after = db.Fresh();
        var reloaded = after.LoopRuns.First(r => r.Id == run.Id);
        Assert.Equal(LoopRunStatus.Cancelled, reloaded.Status);
        Assert.NotNull(reloaded.CompletedAt);
    }
}
