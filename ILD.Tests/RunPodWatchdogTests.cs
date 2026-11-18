using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class RunPodWatchdogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LoopRun SeedRun(TestDb db, string? worktree, bool paused = false)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "1", LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running, IsPaused = paused, WorktreePath = worktree, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static (RunPodWatchdog Watchdog, Mock<ILoopEngine> Engine) Build(TestDb db, Func<Guid, RunTermination?> termination, bool predates = false)
    {
        var host = new Mock<IRunHost>();
        host.Setup(h => h.GetTerminationAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, DateTimeOffset? _, CancellationToken _) => termination(id));
        host.Setup(h => h.PredatesContainers(It.IsAny<string?>())).Returns(predates);
        var engine = new Mock<ILoopEngine>();
        var provider = new ServiceCollection().AddSingleton(db.LoopRuns).BuildServiceProvider();
        return (new RunPodWatchdog(provider.GetRequiredService<IServiceScopeFactory>(), host.Object, engine.Object, NullLogger<RunPodWatchdog>.Instance), engine);
    }

    [Fact]
    public async Task A_running_run_whose_pod_is_gone_is_parked_once_the_pod_is_missing_twice_in_a_row()
    {
        using var db = new TestDb();
        var run = SeedRun(db, "/ild-run/x/repo");
        var (watchdog, engine) = Build(db, _ => new RunTermination(RunTerminationKind.PodLost));

        await watchdog.SweepOnceAsync(Ct);
        engine.Verify(e => e.ParkRunOnLostPodAsync(It.IsAny<Guid>(), It.IsAny<RunTermination>()), Times.Never);
        await watchdog.SweepOnceAsync(Ct);

        engine.Verify(e => e.ParkRunOnLostPodAsync(run.Id, It.Is<RunTermination>(t => t.Kind == RunTerminationKind.PodLost)), Times.Once);
    }

    [Fact]
    public async Task A_pod_that_is_back_at_the_second_check_is_left_alone()
    {
        using var db = new TestDb();
        SeedRun(db, "/ild-run/x/repo");
        RunTermination? answer = new RunTermination(RunTerminationKind.PodLost);
        var (watchdog, engine) = Build(db, _ => answer);

        await watchdog.SweepOnceAsync(Ct);
        answer = null;
        await watchdog.SweepOnceAsync(Ct);
        answer = new RunTermination(RunTerminationKind.PodLost);
        await watchdog.SweepOnceAsync(Ct);

        engine.Verify(e => e.ParkRunOnLostPodAsync(It.IsAny<Guid>(), It.IsAny<RunTermination>()), Times.Never);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("/ild-run/x/repo", true, false)]
    [InlineData("/ild-run/x/repo", false, true)]
    public async Task A_run_with_no_environment_yet_or_one_on_ILDs_own_disk_or_paused_is_not_checked(string? worktree, bool predates, bool paused)
    {
        using var db = new TestDb();
        SeedRun(db, worktree, paused);
        var (watchdog, engine) = Build(db, _ => new RunTermination(RunTerminationKind.PodLost), predates);

        await watchdog.SweepOnceAsync(Ct);
        await watchdog.SweepOnceAsync(Ct);

        engine.Verify(e => e.ParkRunOnLostPodAsync(It.IsAny<Guid>(), It.IsAny<RunTermination>()), Times.Never);
    }
}
