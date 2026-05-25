using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class EventLogRetentionSweeperTests
{
    [Fact]
    public async Task Sweep_deletes_only_events_for_runs_whose_workitem_is_done()
    {
        var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var doneWorkItemId = Guid.NewGuid().ToString();
        var activeWorkItemId = Guid.NewGuid().ToString();
        var doneRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = doneWorkItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume };
        var activeRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = activeWorkItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopRuns.Add(doneRun);
        db.Context.LoopRuns.Add(activeRun);
        db.Context.SaveChanges();

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(doneWorkItemId))
            .ReturnsAsync(new WorkItemView { Id = doneWorkItemId, Status = RemoteWorkItemStatus.Done });
        workItems.Setup(m => m.GetWorkItemAsync(activeWorkItemId))
            .ReturnsAsync(new WorkItemView { Id = activeWorkItemId, Status = RemoteWorkItemStatus.HumanFeedback });

        var svc = new EventLogService(db.EventLogs, db.LoopRuns);
        await svc.AppendAsync(doneRun.Id, "NodeStarted", "from-done");
        await svc.AppendAsync(activeRun.Id, "NodeStarted", "from-active");

        foreach (var e in db.Context.EventLogs)
            e.Timestamp = DateTime.UtcNow.AddDays(-30);
        db.Context.SaveChanges();

        var sweeper = BuildSweeper(db, svc, workItems.Object, retention: TimeSpan.FromDays(7));
        await InvokeSweepOnceAsync(sweeper);

        Assert.Empty((await svc.GetByRunIdAsync(doneRun.Id)));
        Assert.Single((await svc.GetByRunIdAsync(activeRun.Id)));
    }

    private static EventLogRetentionSweeper BuildSweeper(TestDb db, IEventLogService svc, IWorkItemManager workItems, TimeSpan retention)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventLogStore>(db.EventLogs);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(svc);
        services.AddSingleton(workItems);
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        return new EventLogRetentionSweeper(scopes, new EventLogOptions { RetentionPeriod = retention }, NullLogger<EventLogRetentionSweeper>.Instance);
    }

    private static Task InvokeSweepOnceAsync(EventLogRetentionSweeper sweeper) =>
        (Task)typeof(EventLogRetentionSweeper)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(sweeper, new object?[] { CancellationToken.None })!;
}
