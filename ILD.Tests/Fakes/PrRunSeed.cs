using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests.Fakes;

/// <summary>
/// Runs for the PR tests on the real <see cref="ILD.Data.Stores.LoopRunStore"/>, so queue,
/// ledger and poll writes behave as they do against the database instead of as a copy of them.
/// </summary>
internal static class PrRunSeed
{
    public const string DefaultPrUrl = "https://github.com/team/repo/pull/7";

    public static async Task<LoopRun> SeedAsync(
        TestDb db,
        LoopRunStatus status = LoopRunStatus.Running,
        string? prUrl = DefaultPrUrl,
        string workItemId = "wi-1",
        string? queue = null,
        string? ledger = null,
        Guid? currentNodeId = null,
        string? humanFeedbackReason = null,
        Guid? id = null)
    {
        var run = Stage(db, status, prUrl, workItemId, queue, ledger, currentNodeId, humanFeedbackReason, id);
        await db.Context.SaveChangesAsync();
        return run;
    }

    /// <summary>A run parked on its PR node, waiting for the merge: the state the heartbeat's targeted write lands on.</summary>
    public static Task<LoopRun> SeedParkedAsync(TestDb db, Guid currentNodeId, string? ledger)
        => SeedAsync(db, LoopRunStatus.WaitingHuman, ledger: ledger,
            currentNodeId: currentNodeId, humanFeedbackReason: HumanFeedbackReasons.PrAwaitingMerge);

    /// <summary>A seed for harness constructors, which cannot await: an in-memory database completes every call synchronously.</summary>
    public static LoopRun Seed(
        TestDb db,
        LoopRunStatus status = LoopRunStatus.Running,
        string? prUrl = DefaultPrUrl,
        string workItemId = "wi-1",
        Guid? currentNodeId = null,
        string? humanFeedbackReason = null,
        Guid? id = null)
    {
        var run = Stage(db, status, prUrl, workItemId, null, null, currentNodeId, humanFeedbackReason, id);
        db.Context.SaveChanges();
        return run;
    }

    public static LoopRun SeedParked(TestDb db, Guid id, Guid currentNodeId, string workItemId = "wi-1", string? prUrl = DefaultPrUrl)
        => Seed(db, LoopRunStatus.WaitingHuman, prUrl, workItemId, currentNodeId, HumanFeedbackReasons.PrAwaitingMerge, id);

    private static LoopRun Stage(
        TestDb db, LoopRunStatus status, string? prUrl, string workItemId, string? queue, string? ledger,
        Guid? currentNodeId, string? humanFeedbackReason, Guid? id)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow,
        };
        var run = new LoopRun
        {
            Id = id ?? Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = status,
            HumanFeedbackReason = humanFeedbackReason,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            PrUrl = prUrl,
            CurrentNodeId = currentNodeId,
            StartedAt = DateTime.UtcNow,
            PrCommentQueue = queue,
            PrCommentLedger = ledger,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        return run;
    }

    /// <summary>Lets the heartbeat's targeted write through to <paramref name="real"/>, which lands it only on a run parked on that node.</summary>
    public static void RecordPollsOn(this Mock<ILoopRunStore> runs, ILoopRunStore real)
        => runs.Setup(s => s.TryRecordPrPollAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((Guid run, Guid node, string? snapshot, string? states) => real.TryRecordPrPollAsync(run, node, snapshot, states));

    /// <summary>
    /// A store mock whose PR queue, ledger and poll writes go through to <paramref name="real"/>.
    /// Setups for anything else, or a later setup of the same member, are the test's own.
    /// </summary>
    public static Mock<ILoopRunStore> OverRealWrites(ILoopRunStore real)
    {
        var runs = new Mock<ILoopRunStore>();
        runs.RecordPollsOn(real);
        runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>()))
            .Returns((Guid run) => real.GetPrCommentLedgerAsync(run));
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((Guid run, string? expected, string? json) => real.TrySetPrCommentLedgerAsync(run, expected, json));
        runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .Returns((Guid run, string? json) => real.SetPrCommentLedgerAsync(run, json));
        runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>()))
            .Returns((Guid run) => real.GetPrCommentQueueAsync(run));
        runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((Guid run, string? expected, string? json) => real.TrySetPrCommentQueueAsync(run, expected, json));
        return runs;
    }

    /// <summary>Parks a run on its PR node, as the node does when it returns.</summary>
    public static Task ParkAsync(TestDb db, Guid runId)
        => db.Fresh().LoopRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, LoopRunStatus.WaitingHuman)
            .SetProperty(r => r.HumanFeedbackReason, HumanFeedbackReasons.PrAwaitingMerge));

    public static Task SetQueueAsync(TestDb db, Guid runId, string? json)
        => db.Fresh().LoopRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PrCommentQueue, json));

    public static Task SetLedgerAsync(TestDb db, Guid runId, string? json)
        => db.Fresh().LoopRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PrCommentLedger, json));

    public static Task SetPrUrlAsync(TestDb db, Guid runId, string? url)
        => db.Fresh().LoopRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PrUrl, url));

    public static string? QueueOf(TestDb db, Guid runId) => ColumnOf(db, runId, r => r.PrCommentQueue);

    public static string? LedgerOf(TestDb db, Guid runId) => ColumnOf(db, runId, r => r.PrCommentLedger);

    private static string? ColumnOf(TestDb db, Guid runId, System.Linq.Expressions.Expression<Func<LoopRun, string?>> column)
    {
        using var ctx = db.Fresh();
        return ctx.LoopRuns.AsNoTracking().Where(r => r.Id == runId).Select(column).Single();
    }

    public static async Task<LoopRun> ReadAsync(TestDb db, Guid runId)
    {
        await using var ctx = db.Fresh();
        return await ctx.LoopRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }
}
