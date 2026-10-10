using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The event log as the run's timeline: read in the order it was written, closed
/// to conversation once the run has ended, gone only with the run itself, and
/// never shared by two live runs of one work item.
/// </summary>
public class EventLogRunClosureTests
{
    private sealed class Fixture : IDisposable
    {
        public TestDb Db { get; } = new();
        public IEventLogService Events { get; }
        public Guid VersionId { get; }
        public LoopRun Run { get; }
        public LoopRunNode AiRunNode { get; }
        public LoopRunNode CmdRunNode { get; }

        public Fixture()
        {
            Events = RunTimeline.EventLog(Db);
            VersionId = RunTimeline.SeedVersion(Db);
            var ai = RunTimeline.SeedNode(Db, VersionId, NodeType.AI, "Coder");
            var cmd = RunTimeline.SeedNode(Db, VersionId, NodeType.Cmd, "Build");
            Run = RunTimeline.SeedRun(Db, "WI-" + Guid.NewGuid().ToString("N"), VersionId, LoopRunStatus.Running);
            AiRunNode = RunTimeline.SeedRunNode(Db, Run.Id, ai, LoopRunNodeStatus.Succeeded);
            CmdRunNode = RunTimeline.SeedRunNode(Db, Run.Id, cmd, LoopRunNodeStatus.Succeeded);
        }

        public Task<long> AppendAsync(EventType type, string data, LoopRunNode? runNode = null)
            => Events.AppendAsync(Run.Id, type, data, runNode?.LoopNodeId, runNode?.Id);

        public void Dispose() => Db.Dispose();
    }

    public static TheoryData<EventType, EventType> ConversationAfterEnd()
    {
        var data = new TheoryData<EventType, EventType>();
        foreach (var ending in RunTimeline.Ending)
        foreach (var late in new[]
                 {
                     EventType.HumanFeedbackReceived, EventType.HumanFeedbackRequested, EventType.RunParked,
                     EventType.RecoveryTriggered, EventType.LoopRunStarted, EventType.LoopRunCompleted,
                     EventType.LoopRunFailed, EventType.LoopRunCancelled,
                 })
            data.Add(ending, late);
        return data;
    }

    [Theory]
    [MemberData(nameof(ConversationAfterEnd))]
    public async Task A_conversation_event_on_an_ended_run_is_refused_and_not_written(EventType ending, EventType late)
    {
        using var f = new Fixture();
        await f.AppendAsync(ending, "the end");

        var refused = await Record.ExceptionAsync(() => f.AppendAsync(late, "too late"));

        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.Equal("RunClosedException", refused!.GetType().Name);
        Assert.Contains(f.Run.Id.ToString(), refused.Message);
        Assert.DoesNotContain(RunTimeline.Events(f.Db, f.Run.Id), e => e.Data == "too late");
    }

    [Fact]
    public async Task An_AI_turn_on_an_ended_run_is_refused()
    {
        using var f = new Fixture();
        await f.AppendAsync(EventType.LoopRunFailed, "failed");

        var refused = await Record.ExceptionAsync(() => f.AppendAsync(EventType.NodeCompleted, "late answer", f.AiRunNode));

        Assert.Equal("RunClosedException", refused?.GetType().Name);
        Assert.DoesNotContain(RunTimeline.Events(f.Db, f.Run.Id), e => e.Data == "late answer");
    }

    [Theory]
    [InlineData(LoopRunStatus.Completed, EventType.LoopRunCompleted)]
    [InlineData(LoopRunStatus.Failed, EventType.LoopRunFailed)]
    [InlineData(LoopRunStatus.Cancelled, EventType.LoopRunCancelled)]
    public async Task A_terminal_status_closes_conversation_before_the_ending_event_is_written(
        LoopRunStatus status, EventType ending)
    {
        using var f = new Fixture();
        await f.Db.Fresh().LoopRuns.Where(r => r.Id == f.Run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status), TestContext.Current.CancellationToken);

        var refused = await Record.ExceptionAsync(() => f.AppendAsync(EventType.HumanFeedbackReceived, "too late"));

        Assert.Equal("RunClosedException", refused?.GetType().Name);
        Assert.DoesNotContain(RunTimeline.Events(f.Db, f.Run.Id), e => e.Data == "too late");

        await f.AppendAsync(ending, "the end");
        Assert.Equal(ending, Assert.Single(RunTimeline.Events(f.Db, f.Run.Id)).EventType);
    }

    [Theory]
    [InlineData(EventType.NodeStarted)]
    [InlineData(EventType.EdgeTraversed)]
    [InlineData(EventType.NodeFailed)]
    [InlineData(EventType.NodeInterrupted)]
    [InlineData(EventType.CleanupStarted)]
    [InlineData(EventType.CleanupCompleted)]
    [InlineData(EventType.PrMerged)]
    [InlineData(EventType.PrMergeFailed)]
    [InlineData(EventType.BranchDeleteFailed)]
    [InlineData(EventType.PrQueuedWritesClaimed)]
    [InlineData(EventType.Error)]
    public async Task Housekeeping_after_the_end_is_still_recorded(EventType type)
    {
        using var f = new Fixture();
        await f.AppendAsync(EventType.LoopRunCompleted, "done");

        await f.AppendAsync(type, "housekeeping");

        Assert.Equal(type, RunTimeline.Events(f.Db, f.Run.Id).Last().EventType);
    }

    [Fact]
    public async Task A_non_AI_node_completing_after_the_end_is_still_recorded()
    {
        using var f = new Fixture();
        await f.AppendAsync(EventType.LoopRunCancelled, "stopped");

        await f.AppendAsync(EventType.NodeCompleted, "cleanup ran", f.CmdRunNode);

        Assert.Equal("cleanup ran", RunTimeline.Events(f.Db, f.Run.Id).Last().Data);
    }

    [Fact]
    public async Task Ending_one_run_leaves_another_run_open()
    {
        using var f = new Fixture();
        var other = RunTimeline.SeedRun(f.Db, "WI-" + Guid.NewGuid().ToString("N"), f.VersionId, LoopRunStatus.Running);
        await f.AppendAsync(EventType.LoopRunCompleted, "done");

        await f.Events.AppendAsync(other.Id, EventType.HumanFeedbackReceived, "for the other run");

        Assert.Equal("for the other run", Assert.Single(RunTimeline.Events(f.Db, other.Id)).Data);
    }

    [Fact]
    public async Task A_run_is_read_back_in_the_order_its_events_were_written_not_by_timestamp()
    {
        using var f = new Fixture();
        var t = DateTime.UtcNow;
        foreach (var (data, at) in new[] { ("one", t), ("two", t.AddMinutes(-5)), ("three", t.AddMinutes(-10)) })
        {
            f.Db.Context.EventLogs.Add(new EventLog
            {
                LoopRunId = f.Run.Id,
                EventType = EventType.NodeStarted,
                Data = data,
                Timestamp = at,
            });
            await f.Db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var read = (await f.Events.GetByRunIdAsync(f.Run.Id)).Select(e => e.Data);

        Assert.Equal(new[] { "one", "two", "three" }, read);
    }

    [Fact]
    public async Task Deleting_a_run_row_takes_all_its_events_with_it()
    {
        using var f = new Fixture();
        await f.AppendAsync(EventType.LoopRunStarted, "started");
        await f.AppendAsync(EventType.HumanFeedbackReceived, "a reply");
        await f.AppendAsync(EventType.LoopRunCompleted, "done");

        await f.Db.Fresh().LoopRuns.Where(r => r.Id == f.Run.Id)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.Empty(f.Db.Fresh().EventLogs.Where(e => e.LoopRunId == f.Run.Id));
    }

    [Theory]
    [InlineData(LoopRunStatus.Running, LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.Running, LoopRunStatus.WaitingHuman)]
    [InlineData(LoopRunStatus.WaitingHuman, LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.WaitingHuman, LoopRunStatus.WaitingHuman)]
    public void The_database_refuses_a_second_active_run_for_one_work_item(LoopRunStatus existing, LoopRunStatus second)
    {
        using var f = new Fixture();
        var workItemId = "WI-" + Guid.NewGuid().ToString("N");
        RunTimeline.SeedRun(f.Db, workItemId, f.VersionId, existing);

        using var other = f.Db.Fresh();
        other.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = f.VersionId,
            Status = second,
            StartedAt = DateTime.UtcNow,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });

        Assert.Throws<DbUpdateException>(() => other.SaveChanges());
        Assert.Single(f.Db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
    }
}
