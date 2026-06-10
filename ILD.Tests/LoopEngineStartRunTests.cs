using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class LoopEngineStartRunTests
{
    [Fact]
    public async Task StartRun_pins_the_templates_recovery_policy_on_the_run()
    {
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.NeedsReview, seedVersionAndStartNode: true);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal(RecoveryPolicy.NeedsReview, run.RecoveryPolicy);
    }

    [Fact]
    public async Task StartRun_parks_workitem_instead_of_throwing_when_template_has_no_version()
    {
        // An exception here would leave the work item claimed as Running on
        // the server with no run driving it — stuck, holding a slot forever.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, workItems) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: false);

        await engine.StartRunAsync(workItemId);

        Assert.Empty(db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
        workItems.Verify(w => w.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    private static (ILoopEngine engine, Mock<IWorkItemManager> workItems) BuildEngine(
        TestDb db, string workItemId, RecoveryPolicy templatePolicy, bool seedVersionAndStartNode)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = templatePolicy };
        db.Context.LoopTemplates.Add(template);
        if (seedVersionAndStartNode)
        {
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            db.Context.LoopTemplateVersions.Add(version);
            db.Context.LoopNodes.Add(new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = NodeType.Start,
                Label = "start",
            });
        }
        db.Context.SaveChanges();

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(w => w.GetWorkItemAsync(workItemId))
            .ReturnsAsync(new WorkItemView { Id = workItemId, Tags = new[] { "tag" } });
        workItems.Setup(w => w.TransitionAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
            .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, template.Id, new[] { "t" }));

        // The Start node executor just terminates so the background loop exits
        // quickly and deterministically.
        var registry = new ScriptedExecutorRegistry();
        registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"), new NodeOutcome.Terminal("done")));

        var services = new ServiceCollection();
        services.AddSingleton(db.Context);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(db.LoopTemplates);
        services.AddSingleton<IWorkItemManager>(workItems.Object);
        services.AddSingleton(resolver.Object);
        services.AddSingleton<IRunNotifier, NoopRunNotifier>();
        services.AddSingleton<INodeExecutorRegistry>(registry);
        var sp = services.BuildServiceProvider();

        var engine = new LoopEngine(sp, registry, sp.GetRequiredService<IRunNotifier>(), NullLogger<LoopEngine>.Instance);
        return (engine, workItems);
    }

    /// <summary>Waits for the engine's background run loop to park.</summary>
    private static async Task DrainAsync(ILoopEngine engine)
    {
        for (var i = 0; i < 200 && (await engine.GetActiveRunIdsAsync()).Any(); i++)
            await Task.Delay(10);
    }
}
