using ILD.Core.Services.Implementations;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// A conversation page reads only the events it can show, while its cursor still
/// moves past every event the run has, so a poll never reads the same rows twice.
/// </summary>
public class RunConversationReadTests
{
    private sealed class Fixture : IDisposable
    {
        public TestDb Db { get; } = new();
        public LoopRun Run { get; }
        public LoopRunNode Coder { get; }

        public Fixture()
        {
            var versionId = RunTimeline.SeedVersion(Db);
            Run = RunTimeline.SeedRun(Db, "WI-1", versionId, LoopRunStatus.Running);
            Coder = RunTimeline.SeedRunNode(Db, Run.Id, RunTimeline.SeedNode(Db, versionId, NodeType.AI, "Coder"), LoopRunNodeStatus.Succeeded);
        }

        public Task<long> AppendAsync(EventType type, string data, LoopRunNode? runNode = null)
            => Db.EventLogs.AppendAsync(new EventLog
            {
                LoopRunId = Run.Id,
                EventType = type,
                RunNodeId = runNode?.Id,
                NodeId = runNode?.LoopNodeId,
                Data = data,
                Timestamp = DateTime.UtcNow,
            });

        public RunConversationService Service() => new(Db.EventLogs, Db.LoopRuns);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task The_store_reads_only_the_asked_types_up_to_the_bound()
    {
        using var f = new Fixture();
        var started = await f.AppendAsync(EventType.LoopRunStarted, "Run started");
        await f.AppendAsync(EventType.NodeStarted, "a long prompt", f.Coder);
        var ai = await f.AppendAsync(EventType.NodeCompleted, "a plan", f.Coder);
        await f.AppendAsync(EventType.EdgeTraversed, "OnSuccess", f.Coder);
        await f.AppendAsync(EventType.HumanFeedbackReceived, "past the bound");

        var events = await f.Db.EventLogs.GetByRunIdAfterAsync(f.Run.Id, 0, ai, RunConversationEvents.Projected);

        Assert.Equal(new[] { started, ai }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task The_last_id_is_the_highest_event_of_the_run_after_the_cursor_of_any_type()
    {
        using var f = new Fixture();
        var started = await f.AppendAsync(EventType.LoopRunStarted, "Run started");
        var edge = await f.AppendAsync(EventType.EdgeTraversed, "OnSuccess", f.Coder);

        Assert.Equal(edge, await f.Db.EventLogs.GetLastIdByRunIdAfterAsync(f.Run.Id, 0));
        Assert.Equal(edge, await f.Db.EventLogs.GetLastIdByRunIdAfterAsync(f.Run.Id, started));
        Assert.Null(await f.Db.EventLogs.GetLastIdByRunIdAfterAsync(f.Run.Id, edge));
        Assert.Null(await f.Db.EventLogs.GetLastIdByRunIdAfterAsync(Guid.NewGuid(), 0));
    }

    [Fact]
    public async Task Polling_moves_past_events_that_are_not_conversation_and_picks_up_new_turns()
    {
        using var f = new Fixture();
        var service = f.Service();
        await f.AppendAsync(EventType.LoopRunStarted, "Run started");
        await f.AppendAsync(EventType.NodeStarted, "a long prompt", f.Coder);
        var ai = await f.AppendAsync(EventType.NodeCompleted, "a plan", f.Coder);
        var edge = await f.AppendAsync(EventType.EdgeTraversed, "OnSuccess", f.Coder);

        var first = await service.GetPageAsync(f.Run.Id);
        Assert.Equal(edge, first!.LastEventId);
        Assert.Equal(ai, first.Messages[^1].Id);

        var idle = await service.GetPageAsync(f.Run.Id, edge);
        Assert.Empty(idle!.Messages);
        Assert.Equal(edge, idle.LastEventId);

        var housekeeping = await f.AppendAsync(EventType.PrQueuedWritesClaimed, "1 write");
        var quiet = await service.GetPageAsync(f.Run.Id, edge);
        Assert.Empty(quiet!.Messages);
        Assert.Equal(housekeeping, quiet.LastEventId);

        var reply = await f.AppendAsync(EventType.HumanFeedbackReceived, "go on");
        var next = await service.GetPageAsync(f.Run.Id, housekeeping);
        Assert.Equal(new[] { reply }, next!.Messages.Select(m => m.Id));
        Assert.Equal(reply, next.LastEventId);
    }
}
