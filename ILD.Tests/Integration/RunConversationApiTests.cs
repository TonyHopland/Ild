using System.Net;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// A run's event log read back through the API: the events page keyed by event
/// id, the conversation projected from those events, and the
/// <c>{{Conversation.*}}</c> prompt variables built from that same projection.
/// </summary>
public class RunConversationApiTests
{
    private sealed class Seed
    {
        private readonly AppDbContext _db;
        public Guid VersionId { get; }
        private DateTime _clock = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        public Seed(AppDbContext db)
        {
            _db = db;
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            _db.LoopTemplates.Add(template);
            _db.LoopTemplateVersions.Add(version);
            _db.SaveChanges();
            VersionId = version.Id;
        }

        /// <summary>Timestamps run backwards, so only the write order can put the events in order.</summary>
        public bool ClockRunsBackwards { get; init; }

        public LoopNode Node(NodeType type, string label)
        {
            var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = VersionId, NodeType = type, Label = label };
            _db.LoopNodes.Add(node);
            _db.SaveChanges();
            return node;
        }

        public LoopRun Run(LoopRunStatus status = LoopRunStatus.Completed)
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-" + Guid.NewGuid().ToString("N"),
                LoopTemplateVersionId = VersionId,
                Status = status,
                StartedAt = _clock,
                CompletedAt = status == LoopRunStatus.Running ? null : _clock.AddHours(1),
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            };
            _db.LoopRuns.Add(run);
            _db.SaveChanges();
            return run;
        }

        public LoopRunNode RunNode(LoopRun run, LoopNode node, string? label)
        {
            var runNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = node.Id,
                NodeLabel = label,
                Status = LoopRunNodeStatus.Succeeded,
                StartedAt = _clock,
                CompletedAt = _clock,
            };
            _db.LoopRunNodes.Add(runNode);
            _db.SaveChanges();
            return runNode;
        }

        /// <summary>One row, written the way rows were before event ids existed: no edge name, one save per row.</summary>
        public EventLog Event(LoopRun run, EventType type, string data, LoopRunNode? runNode = null)
        {
            _clock = ClockRunsBackwards ? _clock.AddMinutes(-1) : _clock.AddMinutes(1);
            var row = new EventLog
            {
                LoopRunId = run.Id,
                EventType = type,
                Data = data,
                NodeId = runNode?.LoopNodeId,
                RunNodeId = runNode?.Id,
                Timestamp = _clock,
            };
            _db.EventLogs.Add(row);
            _db.SaveChanges();
            return row;
        }
    }

    [Fact]
    public async Task Events_page_by_event_id_with_the_id_as_cursor()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        Guid runId;
        Guid nodeId, runNodeId;
        using (var scope = factory.Services.CreateScope())
        {
            var seed = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var human = seed.Node(NodeType.Human, "Approve");
            var run = seed.Run(LoopRunStatus.Running);
            var rn = seed.RunNode(run, human, "Approve");
            (runId, nodeId, runNodeId) = (run.Id, human.Id, rn.Id);
            var events = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            await events.AppendAsync(runId, EventType.NodeStarted, "started", nodeId, runNodeId);
            await events.AppendAsync(runId, EventType.EdgeTraversed, "approve", nodeId, runNodeId, "approve");
            await events.AppendAsync(runId, EventType.NodeStarted, "next");
        }

        using var first = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/events?cursor=0&limit=2", TestContext.Current.CancellationToken));
        var page1 = first.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, page1.Count);
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(page1[1].GetProperty("id").GetInt64(), first.RootElement.GetProperty("nextCursor").GetInt64());
        Assert.True(page1[0].GetProperty("id").GetInt64() < page1[1].GetProperty("id").GetInt64());
        Assert.False(page1[0].TryGetProperty("sequence", out _));
        var edge = page1[1];
        Assert.Equal(runId, edge.GetProperty("runId").GetGuid());
        Assert.Equal("EdgeTraversed", edge.GetProperty("eventType").GetString());
        Assert.Equal(nodeId, edge.GetProperty("nodeId").GetGuid());
        Assert.Equal(runNodeId, edge.GetProperty("runNodeId").GetGuid());
        Assert.Equal("approve", edge.GetProperty("edgeName").GetString());
        Assert.Equal("approve", edge.GetProperty("payload").GetString());
        Assert.True(edge.TryGetProperty("timestamp", out _));

        var cursor = first.RootElement.GetProperty("nextCursor").GetInt64();
        using var second = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/events?cursor={cursor}&limit=2", TestContext.Current.CancellationToken));
        var last = Assert.Single(second.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("next", last.GetProperty("payload").GetString());
        Assert.True(last.GetProperty("id").GetInt64() > cursor);
        Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
        var lastId = last.GetProperty("id").GetInt64();
        Assert.Equal(lastId, second.RootElement.GetProperty("nextCursor").GetInt64());

        using var empty = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/events?cursor={lastId}", TestContext.Current.CancellationToken));
        Assert.Empty(empty.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(lastId, empty.RootElement.GetProperty("nextCursor").GetInt64());

        var bad = await client.GetAsync("/api/v1/loopruns/not-a-guid/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_conversation_of_a_run_written_before_the_new_events_existed_is_its_ai_turns_and_replies_in_write_order()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        Guid runId;
        EventLog implemented, reply, emptyReply, reviewed;
        using (var scope = factory.Services.CreateScope())
        {
            var seed = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>()) { ClockRunsBackwards = true };
            var implementer = seed.Node(NodeType.AI, "Implementer");
            var reviewer = seed.Node(NodeType.AI, "Reviewer");
            var build = seed.Node(NodeType.Cmd, "Build");
            var run = seed.Run();
            var other = seed.Run();
            var rnImpl = seed.RunNode(run, implementer, "Implementer (retry)");
            var rnRev = seed.RunNode(run, reviewer, null);
            var rnBuild = seed.RunNode(run, build, "Build");
            runId = run.Id;

            seed.Event(run, EventType.NodeStarted, "prompt for the implementer", rnImpl);
            implemented = seed.Event(run, EventType.NodeCompleted, "implemented it", rnImpl);
            seed.Event(run, EventType.NodeCompleted, "build ok", rnBuild);
            seed.Event(run, EventType.NodeCompleted, "", rnRev);
            reply = seed.Event(run, EventType.HumanFeedbackReceived, "Looks good, but rename it");
            emptyReply = seed.Event(run, EventType.HumanFeedbackReceived, "");
            seed.Event(other, EventType.HumanFeedbackReceived, "a reply to another run");
            reviewed = seed.Event(run, EventType.NodeCompleted, "renamed and reviewed", rnRev);
            seed.Event(run, EventType.EdgeTraversed, "OnSuccess", rnRev);
        }

        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/conversation", TestContext.Current.CancellationToken));

        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(
            new[]
            {
                ("ai", "Implementer (retry)", "implemented it"),
                ("human", "Human", "Looks good, but rename it"),
                ("human", "Human", ""),
                ("ai", "Reviewer", "renamed and reviewed"),
            },
            messages.Select(m => (m.GetProperty("role").GetString(), m.GetProperty("name").GetString(), m.GetProperty("text").GetString())));
        Assert.Equal(
            new[] { implemented.Id, reply.Id, emptyReply.Id, reviewed.Id },
            messages.Select(m => m.GetProperty("id").GetInt64()));
        Assert.All(messages, m => Assert.Equal(runId, m.GetProperty("runId").GetGuid()));
        Assert.Equal(implemented.RunNodeId, messages[0].GetProperty("runNodeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("runNodeId").ValueKind);
        Assert.True(messages[3].TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task Run_level_and_park_events_join_the_conversation_as_system_messages_named_for_a_reader()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        Guid runId;
        using (var scope = factory.Services.CreateScope())
        {
            var seed = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var coder = seed.Node(NodeType.AI, "Coder");
            var human = seed.Node(NodeType.Human, "Ask");
            var run = seed.Run();
            var rnCoder = seed.RunNode(run, coder, "Coder");
            var rnHuman = seed.RunNode(run, human, "Ask");
            runId = run.Id;

            seed.Event(run, EventType.LoopRunStarted, "Run started from loop t");
            seed.Event(run, EventType.NodeCompleted, "a plan", rnCoder);
            seed.Event(run, EventType.HumanFeedbackRequested, "Is this plan right?", rnHuman);
            seed.Event(run, EventType.HumanFeedbackReceived, "yes");
            seed.Event(run, EventType.NodeInterrupted, "provider limit", rnCoder);
            seed.Event(run, EventType.RunParked, "Provider throttled the coder; resume when the limit resets", rnCoder);
            seed.Event(run, EventType.RecoveryTriggered, "Recovery requires review");
            seed.Event(run, EventType.NodeFailed, "node blew up", rnCoder);
            seed.Event(run, EventType.LoopRunFailed, "missing edge connection: approve");
        }

        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/conversation", TestContext.Current.CancellationToken));

        Assert.Equal(runId, doc.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(
            new[]
            {
                ("system", "Run started", "Run started from loop t"),
                ("ai", "Coder", "a plan"),
                ("human", "Human", "yes"),
                ("system", "Run parked", "Provider throttled the coder; resume when the limit resets"),
                ("system", "Recovery", "Recovery requires review"),
                ("system", "Run failed", "missing edge connection: approve"),
            },
            doc.RootElement.GetProperty("messages").EnumerateArray()
                .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("name").GetString(), m.GetProperty("text").GetString())));
    }

    [Fact]
    public async Task The_conversation_of_an_unknown_run_is_404_and_of_a_malformed_id_400()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var unknown = await client.GetAsync($"/api/v1/loopruns/{Guid.NewGuid()}/conversation", TestContext.Current.CancellationToken);
        var malformed = await client.GetAsync("/api/v1/loopruns/not-a-guid/conversation", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    private static async Task<JsonDocument> ConversationAsync(HttpClient client, Guid runId, string query = "")
        => JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/conversation{query}", TestContext.Current.CancellationToken));

    private static long? LastEventId(JsonDocument doc)
        => doc.RootElement.GetProperty("lastEventId") is { ValueKind: JsonValueKind.Number } n ? n.GetInt64() : null;

    private static long[] MessageIds(JsonDocument doc)
        => doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt64()).ToArray();

    [Fact]
    public async Task The_conversation_reads_on_after_an_event_id_and_reports_the_last_event_it_covered()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        Guid runId, emptyRunId;
        EventLog started, aiStarted, ai, reply, edge;
        using (var scope = factory.Services.CreateScope())
        {
            var seed = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var coder = seed.Node(NodeType.AI, "Coder");
            var run = seed.Run(LoopRunStatus.Running);
            emptyRunId = seed.Run(LoopRunStatus.Running).Id;
            var rnCoder = seed.RunNode(run, coder, "Coder");
            runId = run.Id;

            started = seed.Event(run, EventType.LoopRunStarted, "Run started");
            aiStarted = seed.Event(run, EventType.NodeStarted, "the prompt", rnCoder);
            ai = seed.Event(run, EventType.NodeCompleted, "a plan", rnCoder);
            reply = seed.Event(run, EventType.HumanFeedbackReceived, "go on");
            edge = seed.Event(run, EventType.EdgeTraversed, "OnSuccess", rnCoder);
        }

        using var whole = await ConversationAsync(client, runId);
        Assert.Equal(new[] { started.Id, ai.Id, reply.Id }, MessageIds(whole));
        Assert.Equal(edge.Id, LastEventId(whole));

        using var later = await ConversationAsync(client, runId, $"?after={aiStarted.Id}");
        Assert.Equal(new[] { ai.Id, reply.Id }, MessageIds(later));
        Assert.Equal(
            new[] { ("ai", "Coder", "a plan"), ("human", "Human", "go on") },
            later.RootElement.GetProperty("messages").EnumerateArray()
                .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("name").GetString(), m.GetProperty("text").GetString())));
        Assert.Equal(edge.Id, LastEventId(later));

        using var caughtUp = await ConversationAsync(client, runId, $"?after={edge.Id}");
        Assert.Empty(MessageIds(caughtUp));
        Assert.True(LastEventId(caughtUp) >= edge.Id);

        using var ahead = await ConversationAsync(client, runId, $"?after={edge.Id + 100}");
        Assert.Empty(MessageIds(ahead));
        Assert.True(LastEventId(ahead) >= edge.Id + 100);

        using var none = await ConversationAsync(client, emptyRunId);
        Assert.Empty(MessageIds(none));
        Assert.True(LastEventId(none) is null or 0);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task A_malformed_or_negative_after_is_400(string after)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        Guid runId;
        using (var scope = factory.Services.CreateScope())
            runId = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>()).Run().Id;

        var response = await client.GetAsync($"/api/v1/loopruns/{runId}/conversation?after={after}", TestContext.Current.CancellationToken);
        var unknown = await client.GetAsync($"/api/v1/loopruns/{Guid.NewGuid()}/conversation?after=0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private const string AllThree = "F:[{{Conversation.Full}}] A:[{{Conversation.AI}}] H:[{{Conversation.Human}}]";

    [Fact]
    public async Task Conversation_prompt_variables_render_the_projected_ai_turns_and_replies_as_before()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var seed = new Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var implementer = seed.Node(NodeType.AI, "Implementer");
        var reviewer = seed.Node(NodeType.AI, "Reviewer");
        var prompt = seed.Node(NodeType.Prompt, "Prompt");
        var human = seed.Node(NodeType.Human, "Ask");
        var run = seed.Run(LoopRunStatus.Running);
        var rnImpl = seed.RunNode(run, implementer, "Implementer");
        var rnPrompt = seed.RunNode(run, prompt, "Prompt");
        var rnHuman = seed.RunNode(run, human, "Ask");
        var rnRev = seed.RunNode(run, reviewer, "Reviewer");

        seed.Event(run, EventType.LoopRunStarted, "Run started");
        seed.Event(run, EventType.NodeCompleted, "first AI message", rnImpl);
        seed.Event(run, EventType.NodeCompleted, "rendered prompt", rnPrompt);
        seed.Event(run, EventType.HumanFeedbackRequested, "please review", rnHuman);
        seed.Event(run, EventType.HumanFeedbackReceived, "second message from a human", rnHuman);
        seed.Event(run, EventType.RunParked, "Run Halted", rnRev);
        seed.Event(run, EventType.NodeCompleted, "third message, also AI", rnRev);

        var rendered = await scope.ServiceProvider.GetRequiredService<IPromptRenderingService>().RenderAsync(
            AllThree, run.Id, new WorkItemView { Id = run.WorkItemId, Title = "Title" }, null);

        Assert.Equal(
            "F:[[AI · Implementer] first AI message\n\n" +
            "[Human] second message from a human\n\n" +
            "[AI · Reviewer] third message, also AI] " +
            "A:[[AI · Implementer] first AI message\n\n[AI · Reviewer] third message, also AI] " +
            "H:[second message from a human]",
            rendered);
    }
}
