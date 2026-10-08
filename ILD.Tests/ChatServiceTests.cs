using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Unit coverage for the standalone chat orchestrator (ADR-0010): lifecycle,
/// the single-turn wrapper, session binding, the interrupt path, and the
/// hard-delete that leaves nothing chat-local behind.
/// </summary>
public sealed class ChatServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RecordingChatNotifier _notifier = new();
    private readonly ChatLoopScratchpad _loopScratchpad = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-tests", Guid.NewGuid().ToString("N"));

    private ChatOptions Options => new() { ScratchRoot = _scratchRoot };

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    /// <summary>Fake adapter that records its context and replays a scripted turn.</summary>
    private sealed class FakeAdapter : IAgentAdapter
    {
        private readonly Func<AgentExecutionContext, Task<NodeExecutionResult>> _run;
        public AgentExecutionContext? LastContext { get; private set; }

        public FakeAdapter(Func<AgentExecutionContext, Task<NodeExecutionResult>> run) => _run = run;

        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            LastContext = context;
            return _run(context);
        }
    }

    private sealed record AppendedMessage(Guid ChatSessionId, ChatMessageView Message);

    private sealed class RecordingChatNotifier : IChatNotifier
    {
        public List<AppendedMessage> Appended { get; } = new();
        public List<string> Progress { get; } = new();
        public List<Guid> Started { get; } = new();
        public List<bool> Completed { get; } = new();

        // The turn that produced each message is recorded alongside it, so a test can
        // tell one turn's messages from another's.
        public List<Guid> AppendedTurnIds { get; } = new();

        public Task MessageAppendedAsync(Guid chatSessionId, Guid turnId, ChatMessageView message)
        {
            AppendedTurnIds.Add(turnId);
            Appended.Add(new AppendedMessage(chatSessionId, message));
            return Task.CompletedTask;
        }

        public List<Guid> ProgressTurnIds { get; } = new();

        public Task TurnProgressAsync(Guid chatSessionId, Guid turnId, string delta)
        {
            ProgressTurnIds.Add(turnId);
            Progress.Add(delta);
            return Task.CompletedTask;
        }

        public Task TurnStartedAsync(Guid chatSessionId, Guid turnId)
        {
            Started.Add(turnId);
            return Task.CompletedTask;
        }

        public Task TurnCompletedAsync(Guid chatSessionId, Guid turnId, bool interrupted)
        {
            Completed.Add(interrupted);
            return Task.CompletedTask;
        }

        public List<string> LoopUpdates { get; } = new();

        public Task LoopUpdateRequestedAsync(Guid chatSessionId, string document)
        {
            LoopUpdates.Add(document);
            return Task.CompletedTask;
        }

        public Task EditProposalsChangedAsync(Guid chatSessionId) => Task.CompletedTask;

        // Each unread hint with how many replies had been announced when it went out,
        // so a test can tell a hint sent after the reply from one sent before it.
        public List<(string UserId, Guid ChatSessionId, int RepliesAppended)> UnreadChanged { get; } = new();

        public Task UnreadChangedAsync(string userId, Guid chatSessionId)
        {
            UnreadChanged.Add((userId, chatSessionId, Appended.Count(a => a.Message.Role == "assistant")));
            return Task.CompletedTask;
        }

        public List<(string UserId, Guid ChatSessionId)> TitleChanged { get; } = new();

        public Task TitleChangedAsync(string userId, Guid chatSessionId)
        {
            TitleChanged.Add((userId, chatSessionId));
            return Task.CompletedTask;
        }

        public Task ActivityChangedAsync(string userId, Guid chatSessionId) => Task.CompletedTask;
        public Task SchedulesChangedAsync(string userId, Guid scheduleId) => Task.CompletedTask;
    }

    /// <summary>
    /// Records each title job the turn hands off, with what had been stored and
    /// announced by then, and hands back a job that never finishes.
    /// </summary>
    private sealed class RecordingTitleScheduler(Func<(bool ReplyStored, int RepliesAnnounced)> observe) : IChatTitleScheduler
    {
        private readonly TaskCompletionSource _never = new();
        public List<(Guid ChatSessionId, string? OpenWorkItemId, int ReplySequence, bool ReplyStored, int RepliesAnnounced)> Scheduled { get; } = new();

        public Task Schedule(Guid chatSessionId, string? openWorkItemId, int replySequence)
        {
            var (stored, announced) = observe();
            Scheduled.Add((chatSessionId, openWorkItemId, replySequence, stored, announced));
            return _never.Task;
        }
    }

    private RecordingTitleScheduler NewTitleScheduler()
        => new(() =>
        {
            using var ctx = _db.Fresh();
            return (ctx.ChatMessages.Any(m => m.Role == "assistant"),
                _notifier.Appended.Count(a => a.Message.Role == "assistant"));
        });

    private ChatSession ReadSession(Guid id)
    {
        using var ctx = _db.Fresh();
        return ctx.ChatSessions.AsNoTracking().Single(c => c.Id == id);
    }

    private static IAgentAdapterRegistry RegistryFor(IAgentAdapter adapter)
        => Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

    private async Task<AiProvider> SeedProviderAsync(string type = "claude-code", string? config = null)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "p1",
            Type = type,
            BaseUrl = "http://localhost",
            Model = "m",
            Parallelism = 1,
            Config = config,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();
        return provider;
    }

    private ChatService NewService(IAgentAdapter adapter)
        => new(_db.Context, _db.Providers, RegistryFor(adapter), _notifier, Options, _db.LoopRuns, _loopScratchpad);

    /// <summary>
    /// Seed an active (Running) run for <paramref name="workItemId"/> pointing at a
    /// freshly-created worktree directory, so the Chat Context can resolve and
    /// grant it. Returns the worktree path (cleaned up on Dispose with the root).
    /// </summary>
    private async Task<string> SeedActiveRunAsync(string workItemId, LoopRunStatus status = LoopRunStatus.Running)
    {
        var worktreePath = Path.Combine(_scratchRoot, "worktrees", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreePath);

        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"t-{Guid.NewGuid():N}" };
        _db.Context.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = status,
            WorktreePath = worktreePath,
            StartedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync();
        return worktreePath;
    }

    [Fact]
    public async Task StartAsync_creates_session_with_scratch_dir_and_normalized_tools()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));

        var view = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" }, TestContext.Current.CancellationToken);

        Assert.Equal(provider.Id, view.AiProviderId);
        Assert.Contains("ild", view.Tools);
        var session = _db.Context.ChatSessions.Single();
        Assert.Equal("alice", session.UserId);
        Assert.True(Directory.Exists(session.ScratchPath), "scratch directory should be created");
    }

    [Fact]
    public async Task StartAsync_for_copilot_with_omitted_tools_turns_ild_on()
    {
        var provider = await SeedProviderAsync("copilot");
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));

        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ild" }, view.Tools);
        Assert.Equal("ild", _db.Context.ChatSessions.Single().ToolAllowlistCsv);
    }

    [Fact]
    public async Task StartAsync_for_copilot_with_ild_ticked_hands_every_turn_ild()
    {
        var provider = await SeedProviderAsync("copilot");
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);

        var view = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hi", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

        Assert.Equal(new[] { "ild" }, view.Tools);
        Assert.Equal(new[] { "ild" }, adapter.LastContext!.ToolAllowlist);
    }

    [Fact]
    public async Task StartAsync_for_copilot_with_an_empty_selection_keeps_ild_off_for_every_turn()
    {
        var provider = await SeedProviderAsync("copilot");
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);

        var view = await svc.StartAsync("alice", provider.Id, Array.Empty<string>(), TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hi", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

        Assert.Empty(view.Tools);
        Assert.Equal(string.Empty, _db.Context.ChatSessions.Single().ToolAllowlistCsv);
        // An explicit empty list, not null: null would mean "defaults" (ILD on).
        Assert.NotNull(adapter.LastContext!.ToolAllowlist);
        Assert.Empty(adapter.LastContext.ToolAllowlist!);
    }

    [Fact]
    public async Task StartAsync_allows_many_retained_chats_for_the_same_user()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));

        var first = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var second = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _db.Context.ChatSessions.Count(c => c.UserId == "alice"));
    }

    [Fact]
    public async Task ExecuteTurnAsync_appends_turn_binds_session_and_streams_progress()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(async ctx =>
        {
            ctx.OnSessionId?.Invoke("sess-1");
            await ctx.ProgressCallback!("hello ");
            return NodeExecutionResult.Ok("hello world", sessionId: "sess-1");
        });
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        var turnId = Guid.NewGuid();
        await svc.ExecuteTurnAsync(started.Id, turnId, "hi there", CancellationToken.None);

        // The synthesized context routes through the chat session, not a run.
        Assert.Equal(started.Id, adapter.LastContext!.ChatSessionId);
        Assert.True(adapter.LastContext.ManageSession);

        var messages = _db.Context.ChatMessages
            .Where(m => m.ChatSessionId == started.Id)
            .OrderBy(m => m.Sequence)
            .ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("hi there", messages[0].Content);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("hello world", messages[1].Content);
        Assert.False(messages[1].Interrupted);

        var session = _db.Context.ChatSessions.Single();
        Assert.Equal("sess-1", session.CurrentSessionId);

        Assert.Contains("hello ", _notifier.Progress);
        Assert.Equal(2, _notifier.Appended.Count);
        // A turn's start and end belong to whoever started it — only the runner can
        // name the turn, and only it sees a turn that ends without the service
        // saying anything. A second completion from here would clear the bubble's
        // indicator for whichever turn is running by then.
        Assert.Empty(_notifier.Started);
        Assert.Empty(_notifier.Completed);

        // Every event the service publishes says which turn produced it, under the id
        // the runner gave it: the user message, each streamed delta and the finalized
        // reply. The client tells a live turn's traffic from a replaced one's by
        // exactly this id, so an event that cannot name its turn is one the client
        // cannot place.
        Assert.NotEmpty(_notifier.ProgressTurnIds);
        Assert.Equal(2, _notifier.AppendedTurnIds.Count);
        Assert.All(_notifier.AppendedTurnIds, id => Assert.Equal(turnId, id));
        Assert.All(_notifier.ProgressTurnIds, id => Assert.Equal(turnId, id));
    }

    [Fact]
    public async Task ExecuteTurnAsync_resumes_the_bound_session_on_the_next_turn()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(ctx =>
            Task.FromResult(NodeExecutionResult.Ok("ok", sessionId: "sess-1")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "first", CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "second", CancellationToken.None);

        // The second turn must resume the session id captured by the first.
        Assert.Equal("sess-1", adapter.LastContext!.SessionId);
    }

    [Fact]
    public async Task ExecuteTurnAsync_keeps_partial_reply_flagged_interrupted_when_cancelled()
    {
        var provider = await SeedProviderAsync();
        using var cts = new CancellationTokenSource();
        var adapter = new FakeAdapter(async ctx =>
        {
            await ctx.ProgressCallback!("partial answer");
            cts.Cancel();
            // Adapters surface a cancelled turn as a failed result after killing
            // the process; the partial streamed text is what we keep.
            return NodeExecutionResult.Fail("interrupted");
        });
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "go", cts.Token);

        var assistant = _db.Context.ChatMessages
            .Where(m => m.ChatSessionId == started.Id && m.Role == "assistant")
            .Single();
        Assert.True(assistant.Interrupted);
        Assert.Equal("partial answer", assistant.Content);
        // That the cancelled turn also REPORTS itself interrupted is the runner's
        // job now: ChatTurnLifecycleTests covers it, under the turn's own id.
    }

    [Fact]
    public async Task ExecuteTurnAsync_without_open_work_item_sends_the_raw_message_and_no_extra_dirs()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "plain message", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

        Assert.Equal("plain message", adapter.LastContext!.Prompt);
        Assert.Null(adapter.LastContext.AdditionalAllowedDirectories);
    }

    [Fact]
    public async Task ExecuteTurnAsync_pushes_open_work_item_id_into_the_prompt_preamble()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        // No filesystem grant and no active run: id-only context, scratch alone.
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "what is open?", "wi-42", openLoopDocument: null, CancellationToken.None);

        var prompt = adapter.LastContext!.Prompt;
        Assert.Contains("[Chat Context]", prompt);
        Assert.Contains("wi-42", prompt);
        // The human's verbatim message is still appended after the preamble.
        Assert.EndsWith("what is open?", prompt);
        // No active run + no filesystem grant ⇒ no worktree grant.
        Assert.Null(adapter.LastContext.AdditionalAllowedDirectories);

        // The persisted transcript keeps the human's message verbatim (no preamble).
        var userMessage = _db.Context.ChatMessages
            .Single(m => m.ChatSessionId == started.Id && m.Role == "user");
        Assert.Equal("what is open?", userMessage.Content);
    }

    [Fact]
    public async Task ExecuteTurnAsync_grants_active_run_worktree_when_filesystem_grant_held()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "write" }, TestContext.Current.CancellationToken);
        var worktreePath = await SeedActiveRunAsync("wi-99");

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "edit it", "wi-99", openLoopDocument: null, CancellationToken.None);

        Assert.NotNull(adapter.LastContext!.AdditionalAllowedDirectories);
        Assert.Contains(worktreePath, adapter.LastContext.AdditionalAllowedDirectories!);
        Assert.Contains(worktreePath, adapter.LastContext.Prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_withholds_worktree_when_session_lacks_a_filesystem_grant()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        // Only the `ild` tool — no read/write/execute, so the worktree stays hidden
        // even though the open item has an active run.
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var worktreePath = await SeedActiveRunAsync("wi-99");

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "edit it", "wi-99", openLoopDocument: null, CancellationToken.None);

        Assert.Null(adapter.LastContext!.AdditionalAllowedDirectories);
        Assert.DoesNotContain(worktreePath, adapter.LastContext.Prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_withholds_worktree_for_a_finished_run()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" }, TestContext.Current.CancellationToken);
        // A completed run keeps its worktree on disk (ADR-0008) but is not active,
        // so the chat must not expose it (ADR-0011 active-run-only).
        await SeedActiveRunAsync("wi-7", LoopRunStatus.Completed);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "look", "wi-7", openLoopDocument: null, CancellationToken.None);

        Assert.Null(adapter.LastContext!.AdditionalAllowedDirectories);
    }

    [Fact]
    public async Task ExecuteTurnAsync_stashes_the_open_loop_and_flags_it_without_inlining_the_json()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        const string document = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"My Loop\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "tidy this loop", openWorkItemId: null, document, CancellationToken.None);

        // The flag enters the model context, the heavy JSON does not (it is pulled
        // on demand via get_current_loop).
        var prompt = adapter.LastContext!.Prompt;
        Assert.Contains("[Chat Context]", prompt);
        Assert.Contains("Loop Editor", prompt);
        Assert.Contains("get_current_loop", prompt);
        // The heavy document body (its name/nodes) is not inlined — only the flag.
        Assert.DoesNotContain("My Loop", prompt);
        Assert.EndsWith("tidy this loop", prompt);

        // The document itself is stashed in the scratchpad for the agent to pull.
        Assert.Equal(document, _loopScratchpad.Get(started.Id));
    }

    [Fact]
    public async Task ExecuteTurnAsync_stashes_a_v1_loop_document_upgraded_to_v2()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        // An editor tab opened before the upgrade still sends the old format.
        const string legacy =
            "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Old\",\"nodes\":[" +
            "{\"id\":\"review\",\"type\":\"Human\",\"label\":\"Review\",\"config\":{\"customEdges\":[\"Respond\"]}}]," +
            "\"edges\":[{\"id\":\"e1\",\"sourceNodeId\":\"review\",\"targetNodeId\":\"done\",\"edgeType\":\"Custom\",\"name\":\"Respond\"}]}";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "tidy this loop", openWorkItemId: null, legacy, CancellationToken.None);

        var stashed = System.Text.Json.Nodes.JsonNode.Parse(_loopScratchpad.Get(started.Id)!)!;
        Assert.Equal("ild-loop-template/v2", (string)stashed["$schema"]!);
        var review = stashed["nodes"]![0]!["config"]!.AsObject();
        Assert.False(review.ContainsKey("customEdges"));
        Assert.Contains(review["outputs"]!.AsArray(), o => (string)o!["name"]! == "Respond");
    }

    [Fact]
    public async Task ExecuteTurnAsync_includes_node_variable_and_session_guidance_when_a_loop_is_open()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        const string document = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"L\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "help me wire this up", openWorkItemId: null, document, CancellationToken.None);

        // The Chat Context teaches the agent the loop model so it can author a valid
        // document: node types, edges, variables, and sessions.
        var prompt = adapter.LastContext!.Prompt;
        Assert.Contains("Loop authoring guide", prompt);
        Assert.Contains("Condition", prompt);
        Assert.Contains("{{Var.<name>}}", prompt);
        Assert.Contains("sessionPlaceholder", prompt);
        // OnFailure edges are advised sparingly: transient failures should fail in
        // place for a human restart rather than route to Cleanup.
        Assert.Contains("OnFailure edges sparingly", prompt);
        Assert.Contains("fails in place", prompt);
        // The two failure modes an agent cannot discover by reading the document:
        // an edge naming a missing node vanishes without an error, and every node
        // must be wired in or the human's save is rejected wholesale.
        Assert.Contains("silently dropped", prompt);
        Assert.Contains("reachable from Start", prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_omits_loop_guidance_when_only_a_work_item_is_open()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "what is open?", "wi-42", openLoopDocument: null, CancellationToken.None);

        // No loop editor open ⇒ the loop primer is not paid for.
        Assert.DoesNotContain("Loop authoring guide", adapter.LastContext!.Prompt);
    }

    // ---------------------------------------------------------------------
    // A chat turn is not a template. The preamble and the human's message are
    // ambient text for the model, so whatever the service composes must reach
    // the agent CLI byte-for-byte — nothing in the pipeline may expand, strip,
    // or otherwise rewrite it. These tests drive a real CLI adapter against a
    // fake `claude` binary and assert on the prompt that binary was handed.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExecuteTurnAsync_still_gives_the_adapter_a_run_context_for_the_session_scratch_dir()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "hi", CancellationToken.None);

        // A chat turn has no run, but adapters read the run context for the
        // agent's cwd and its session key — it carries the session, not a
        // template context, so it stays even though nothing is rendered.
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.Equal(started.Id, adapter.LastContext!.RunContext.LoopRunId);
        Assert.Equal(scratchPath, adapter.LastContext.RunContext.WorktreePath);
    }

    [Fact]
    public async Task ExecuteTurnAsync_delivers_the_loop_authoring_guide_to_the_cli_with_every_placeholder_intact()
    {
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var adapter = new RecordingAdapter(new ClaudeCodeAdapter());
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        const string document = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"L\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "how do loop variables work?", openWorkItemId: null, document, CancellationToken.None);

        var sent = cli.CapturedPrompt;
        // What the service composed is exactly what the agent process received.
        Assert.Equal(adapter.LastContext!.Prompt, sent);
        // The guide teaches the placeholder grammar by quoting it, so every form
        // it names has to survive the trip literally — an emptied token would
        // teach the agent the wrong syntax and can end up written into a loop.
        Assert.Contains("{{WorkItem.Title}}/{{WorkItem.Description}}", sent);
        Assert.Contains("{{PreviousNode.Output}}", sent);
        Assert.Contains("{{EventLog.LastN}}", sent);
        Assert.Contains("{{Var.<name>}}", sent);
        Assert.Contains("{{Node.Input}}", sent);
    }

    [Fact]
    public async Task ExecuteTurnAsync_delivers_a_user_message_containing_placeholders_to_the_cli_verbatim()
    {
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var adapter = new RecordingAdapter(new ClaudeCodeAdapter());
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        // The reported symptom: a human asking about the syntax had their own
        // question rewritten before the agent ever saw it. The angle-bracket
        // form is the shape the report named; both must arrive untouched.
        const string message = "Why do {{WorkItem.Title}}, {{Var.handoff}} and <Foo.Bar> vanish from my prompt?";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), message, CancellationToken.None);

        Assert.Equal(message, cli.CapturedPrompt);
        Assert.Equal(adapter.LastContext!.Prompt, cli.CapturedPrompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_does_not_inline_scratch_files_named_by_a_worktree_placeholder()
    {
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var adapter = new RecordingAdapter(new ClaudeCodeAdapter());
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" }, TestContext.Current.CancellationToken);

        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        File.WriteAllText(Path.Combine(scratchPath, "notes.txt"), "INLINED-FILE-BODY");

        const string message = "What does {{WorkTree.File:notes.txt}} mean?";
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), message, CancellationToken.None);

        // Chat has no file-inlining side channel: the agent already runs with
        // scratch as its cwd and reads files with its own tools.
        Assert.Equal(message, cli.CapturedPrompt);
        Assert.DoesNotContain("INLINED-FILE-BODY", cli.CapturedPrompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_overwrites_then_clears_the_loop_scratchpad_per_message()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        const string first = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"v1\",\"nodes\":[]}";
        const string second = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"v2\",\"nodes\":[]}";

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "first", openWorkItemId: null, first, CancellationToken.None);
        Assert.Equal(first, _loopScratchpad.Get(started.Id));

        // A later message with a new document overwrites the prior snapshot…
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "second", openWorkItemId: null, second, CancellationToken.None);
        Assert.Equal(second, _loopScratchpad.Get(started.Id));

        // …and a message sent with the editor closed clears it so the agent sees no
        // loop, and the preamble no longer mentions the Loop Editor.
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "third", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);
        Assert.Null(_loopScratchpad.Get(started.Id));
        Assert.DoesNotContain("Loop Editor", adapter.LastContext!.Prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_names_the_chat_from_the_first_user_message()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        Assert.Null(started.Name);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "Help me wire up a deploy loop", CancellationToken.None);
        Assert.Equal("Help me wire up a deploy loop", _db.Context.ChatSessions.Single().Name);

        // The name is fixed by the first turn — a later message must not rename it.
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "now add a PR node", CancellationToken.None);
        Assert.Equal("Help me wire up a deploy loop", _db.Context.ChatSessions.Single().Name);
    }

    [Fact]
    public async Task ExecuteTurnAsync_truncates_a_long_first_message_into_the_name()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        var longMessage = new string('a', 200);
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), longMessage, CancellationToken.None);

        var name = _db.Context.ChatSessions.Single().Name!;
        Assert.Equal(new string('a', 119) + "…", name);
    }

    [Fact]
    public async Task ExecuteTurnAsync_names_the_chat_from_its_first_message_without_the_markdown()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "## Fix the **login** page", CancellationToken.None);

        var session = ReadSession(started.Id);
        Assert.Equal("Fix the login page", session.Name);
        Assert.Equal(ChatTitleSource.Fallback, session.TitleSource);
    }

    [Fact]
    public async Task Each_successful_turn_of_an_untitled_chat_hands_a_title_job_off_after_its_reply_is_stored_and_announced_without_waiting_for_it()
    {
        var provider = await SeedProviderAsync();
        var titles = NewTitleScheduler();
        var svc = new ChatService(_db.Context, _db.Providers,
            RegistryFor(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("reply")))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad, titles: titles);
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        // The job handed off never finishes; the turn must end regardless.
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "first", openWorkItemId: "WI-7", openLoopDocument: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "second", openWorkItemId: "WI-7", openLoopDocument: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The jobs never title the chat, so the second turn tries again, with its own reply.
        Assert.Equal(new[] { (chat.Id, (string?)"WI-7", 1, true, 1), (chat.Id, (string?)"WI-7", 3, true, 2) }, titles.Scheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_first_reply_that_failed_or_was_interrupted_hands_off_no_title_job(bool interrupted)
    {
        var provider = await SeedProviderAsync();
        var titles = NewTitleScheduler();
        using var cts = new CancellationTokenSource();
        var svc = new ChatService(_db.Context, _db.Providers,
            RegistryFor(new FakeAdapter(_ =>
            {
                if (interrupted) cts.Cancel();
                return Task.FromResult(NodeExecutionResult.Fail(interrupted ? "interrupted" : "provider unavailable"));
            })),
            _notifier, Options, _db.LoopRuns, _loopScratchpad, titles: titles);
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "first", cts.Token);

        Assert.Empty(titles.Scheduled);
        Assert.Equal("first", ReadSession(chat.Id).Name);
    }

    [Fact]
    public async Task RenameAsync_names_the_owners_chat_manually_and_hints_the_owner()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hello there", CancellationToken.None);

        Assert.False(await svc.RenameAsync("bob", chat.Id, "Bob's name", TestContext.Current.CancellationToken));
        Assert.False(await svc.RenameAsync("alice", Guid.NewGuid(), "Nobody's", TestContext.Current.CancellationToken));
        Assert.Equal("hello there", ReadSession(chat.Id).Name);
        Assert.Empty(_notifier.TitleChanged);

        Assert.True(await svc.RenameAsync("alice", chat.Id, "Deploy loop wiring", TestContext.Current.CancellationToken));

        var session = ReadSession(chat.Id);
        Assert.Equal("Deploy loop wiring", session.Name);
        Assert.Equal(ChatTitleSource.Manual, session.TitleSource);
        Assert.Equal(new[] { ("alice", chat.Id) }, _notifier.TitleChanged);
        Assert.Equal("Deploy loop wiring", (await svc.ListForUserAsync("alice", TestContext.Current.CancellationToken)).Single().Name);
    }

    [Fact]
    public async Task A_chat_renamed_before_its_first_message_keeps_its_name()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        Assert.True(await svc.RenameAsync("alice", chat.Id, "My own name", TestContext.Current.CancellationToken));
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hello there", CancellationToken.None);

        var session = ReadSession(chat.Id);
        Assert.Equal("My own name", session.Name);
        Assert.Equal(ChatTitleSource.Manual, session.TitleSource);
    }

    [Fact]
    public async Task A_rename_landing_while_the_first_reply_is_written_survives_the_turn_saving_its_session()
    {
        var provider = await SeedProviderAsync();
        // A concurrent PUT /name has its own request scope, so its own context.
        using var otherContext = _db.Fresh();
        var otherRequest = new ChatService(
            otherContext, _db.Providers, RegistryFor(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok()))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad);
        var svc = NewService(new FakeAdapter(async ctx =>
        {
            Assert.True(await otherRequest.RenameAsync("alice", ctx.ChatSessionId!.Value, "My own name", CancellationToken.None));
            return NodeExecutionResult.Ok("reply");
        }));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hello there", CancellationToken.None);

        var session = ReadSession(chat.Id);
        Assert.Equal("My own name", session.Name);
        Assert.Equal(ChatTitleSource.Manual, session.TitleSource);
    }

    [Fact]
    public async Task ListForUserAsync_returns_only_the_users_chats_newest_activity_first()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));

        var older = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(older.Id, Guid.NewGuid(), "first chat", CancellationToken.None);
        var newer = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(newer.Id, Guid.NewGuid(), "second chat", CancellationToken.None);
        // A different user's chat must never leak into alice's history.
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(bobs.Id, Guid.NewGuid(), "bob chat", CancellationToken.None);

        // Force the newer chat to have the most recent activity timestamp.
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {DateTime.UtcNow.AddMinutes(-10)} WHERE \"Id\" = {older.Id}", TestContext.Current.CancellationToken);
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {DateTime.UtcNow} WHERE \"Id\" = {newer.Id}", TestContext.Current.CancellationToken);

        var history = await svc.ListForUserAsync("alice", TestContext.Current.CancellationToken);

        Assert.Equal(2, history.Count);
        Assert.Equal(newer.Id, history[0].Id);
        Assert.Equal(older.Id, history[1].Id);
        Assert.Equal("second chat", history[0].Name);
    }

    [Fact]
    public async Task GetByIdAsync_is_scoped_to_the_owning_user()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "hi there", CancellationToken.None);

        var owner = await svc.GetByIdAsync("alice", started.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(owner);
        Assert.Equal(2, owner!.Messages.Count);

        // Another user may not resume alice's chat.
        Assert.Null(await svc.GetByIdAsync("bob", started.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistsForUserAsync_is_true_only_for_the_owner()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        Assert.True(await svc.ExistsForUserAsync("alice", started.Id, TestContext.Current.CancellationToken));
        // A different user, and a missing id, are both unauthorized/absent.
        Assert.False(await svc.ExistsForUserAsync("bob", started.Id, TestContext.Current.CancellationToken));
        Assert.False(await svc.ExistsForUserAsync("alice", Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetOwnerAsync_names_the_chats_owner_and_null_once_it_is_gone()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var alices = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        Assert.Equal("alice", await svc.GetOwnerAsync(alices.Id, TestContext.Current.CancellationToken));
        Assert.Equal("bob", await svc.GetOwnerAsync(bobs.Id, TestContext.Current.CancellationToken));
        Assert.Null(await svc.GetOwnerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.True(await svc.DeleteAsync("alice", alices.Id, TestContext.Current.CancellationToken));
        Assert.Null(await svc.GetOwnerAsync(alices.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_hard_deletes_one_chat_scoped_to_the_owner()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("x"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "hi", CancellationToken.None);

        // Bind a snapshot to the chat session so we can prove the cascade.
        var snapshots = new AdapterSessionSnapshotStore(_db.Context);
        await snapshots.UpsertForChatAsync(started.Id, "fake", "sess-1", "{\"events\":[]}", TestContext.Current.CancellationToken);
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.True(Directory.Exists(scratchPath));

        // A non-owner cannot delete it.
        Assert.False(await svc.DeleteAsync("bob", started.Id, TestContext.Current.CancellationToken));
        Assert.Single(_db.Context.ChatSessions);

        var deleted = await svc.DeleteAsync("alice", started.Id, TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.Empty(_db.Context.ChatSessions);
        Assert.Empty(_db.Context.ChatMessages);
        Assert.Empty(_db.Context.AdapterSessionSnapshots);
        Assert.False(Directory.Exists(scratchPath), "scratch directory should be removed");
    }

    [Fact]
    public async Task DeleteAllForUserAsync_removes_every_chat_the_user_owns_and_no_others()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var a1 = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var a2 = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var a1Scratch = _db.Context.ChatSessions.Single(c => c.Id == a1.Id).ScratchPath;

        var removed = await svc.DeleteAllForUserAsync("alice", TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(a1Scratch), "scratch directories should be removed");
        Assert.DoesNotContain(_db.Context.ChatSessions, c => c.Id == a1.Id || c.Id == a2.Id);
        // Bob's chat is untouched.
        Assert.Contains(_db.Context.ChatSessions, c => c.Id == bobs.Id);
    }

    private static async Task<bool> HasUnreadAsync(ChatService svc, string userId, Guid chatSessionId)
        => (await svc.ListForUserAsync(userId, TestContext.Current.CancellationToken))
            .Single(c => c.Id == chatSessionId).HasUnread;

    [Fact]
    public async Task A_send_marks_the_chat_read_so_only_a_reply_arriving_after_it_makes_the_chat_unread()
    {
        var provider = await SeedProviderAsync();
        ChatService? svc = null;
        var unreadWhileWaitingForReply = new List<bool>();
        svc = NewService(new FakeAdapter(async ctx =>
        {
            unreadWhileWaitingForReply.Add(await HasUnreadAsync(svc!, "alice", ctx.ChatSessionId!.Value));
            return NodeExecutionResult.Ok("reply");
        }));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "first", CancellationToken.None);
        Assert.True(await HasUnreadAsync(svc, "alice", chat.Id));

        // The second send covers the first reply, which the user never opened.
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "second", CancellationToken.None);

        Assert.Equal(new[] { false, false }, unreadWhileWaitingForReply);
        Assert.True(await HasUnreadAsync(svc, "alice", chat.Id));
    }

    [Fact]
    public async Task A_chat_with_no_read_marker_is_never_unread()
    {
        // Every chat that predates read markers looks like this until its next send.
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var legacy = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            AiProviderId = provider.Id,
            ProviderType = provider.Type,
            ToolAllowlistCsv = "ild",
            ScratchPath = Path.Combine(_scratchRoot, "legacy"),
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.ChatSessions.Add(legacy);
        _db.Context.ChatMessages.AddRange(
            new ChatMessage { Id = Guid.NewGuid(), ChatSessionId = legacy.Id, Role = "user", Content = "hi", Sequence = 0, CreatedAt = DateTime.UtcNow },
            new ChatMessage { Id = Guid.NewGuid(), ChatSessionId = legacy.Id, Role = "assistant", Content = "hello", Sequence = 1, CreatedAt = DateTime.UtcNow });
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(await HasUnreadAsync(svc, "alice", legacy.Id));
    }

    [Fact]
    public async Task MarkReadAsync_raises_the_owners_marker_and_never_lowers_it()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("reply"))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", CancellationToken.None);
        _notifier.UnreadChanged.Clear();

        // Another user cannot read alice's chat for her.
        Assert.False(await svc.MarkReadAsync("bob", chat.Id, 1, TestContext.Current.CancellationToken));
        Assert.True(await HasUnreadAsync(svc, "alice", chat.Id));
        Assert.Empty(_notifier.UnreadChanged);

        Assert.True(await svc.MarkReadAsync("alice", chat.Id, 1, TestContext.Current.CancellationToken));
        Assert.False(await HasUnreadAsync(svc, "alice", chat.Id));
        Assert.Equal(new[] { ("alice", chat.Id) }, _notifier.UnreadChanged.Select(u => (u.UserId, u.ChatSessionId)));

        // A lower or equal sequence is not a raise: nothing moves and nobody is told.
        Assert.False(await svc.MarkReadAsync("alice", chat.Id, 0, TestContext.Current.CancellationToken));
        Assert.False(await svc.MarkReadAsync("alice", chat.Id, 1, TestContext.Current.CancellationToken));
        Assert.Single(_notifier.UnreadChanged);

        // Read ahead of the next send (from another tab, say): the send must not pull
        // the marker back down to its own message, so the reply below it stays read.
        Assert.True(await svc.MarkReadAsync("alice", chat.Id, 100, TestContext.Current.CancellationToken));
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "again", CancellationToken.None);
        Assert.False(await HasUnreadAsync(svc, "alice", chat.Id));
    }

    [Fact]
    public async Task A_send_that_reads_an_unread_chat_hints_its_owner_before_the_reply()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("reply"))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "first", CancellationToken.None);
        Assert.True(await HasUnreadAsync(svc, "alice", chat.Id));
        _notifier.UnreadChanged.Clear();

        // Sent from one window while another shows the first reply as unread: the
        // other window hears of it before the next reply is stored.
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "second", CancellationToken.None);

        Assert.Equal(new[] { ("alice", chat.Id, 1), ("alice", chat.Id, 2) }, _notifier.UnreadChanged);

        // A send that raises nothing, because the chat is already read beyond it,
        // leaves only the reply's own hint.
        Assert.True(await svc.MarkReadAsync("alice", chat.Id, 100, TestContext.Current.CancellationToken));
        _notifier.UnreadChanged.Clear();
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "third", CancellationToken.None);

        Assert.Equal(new[] { ("alice", chat.Id, 3) }, _notifier.UnreadChanged);
    }

    [Fact]
    public async Task Deleting_a_chat_hints_its_owner_and_nobody_else()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("reply"))));
        var one = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var two = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var three = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(one.Id, Guid.NewGuid(), "hi", CancellationToken.None);
        _notifier.UnreadChanged.Clear();

        Assert.False(await svc.DeleteAsync("bob", one.Id, TestContext.Current.CancellationToken));
        Assert.Empty(_notifier.UnreadChanged);

        Assert.True(await svc.DeleteAsync("alice", one.Id, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { ("alice", one.Id) }, _notifier.UnreadChanged.Select(u => (u.UserId, u.ChatSessionId)));

        _notifier.UnreadChanged.Clear();
        Assert.Equal(2, await svc.DeleteAllForUserAsync("alice", TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { ("alice", two.Id), ("alice", three.Id) }.OrderBy(u => u.Item2),
            _notifier.UnreadChanged.Select(u => (u.UserId, u.ChatSessionId)).OrderBy(u => u.ChatSessionId));
    }

    [Fact]
    public async Task A_mark_read_landing_while_the_reply_is_written_survives_the_turn_saving_its_session()
    {
        var provider = await SeedProviderAsync();
        // A concurrent POST /read has its own request scope, so its own context.
        using var otherContext = _db.Fresh();
        var otherRequest = new ChatService(
            otherContext, _db.Providers, RegistryFor(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok()))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad);
        var svc = NewService(new FakeAdapter(async ctx =>
        {
            // The reply will be sequence 1; this mark-read lands before it is stored.
            Assert.True(await otherRequest.MarkReadAsync("alice", ctx.ChatSessionId!.Value, 1, CancellationToken.None));
            return NodeExecutionResult.Ok("reply");
        }));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", CancellationToken.None);

        Assert.False(await HasUnreadAsync(otherRequest, "alice", chat.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_finalized_reply_hints_its_owner_once_it_is_stored(bool interrupted)
    {
        var provider = await SeedProviderAsync();
        using var cts = new CancellationTokenSource();
        var svc = NewService(new FakeAdapter(async ctx =>
        {
            await ctx.ProgressCallback!("partial");
            if (!interrupted) return NodeExecutionResult.Ok("reply");
            cts.Cancel();
            return NodeExecutionResult.Fail("interrupted");
        }));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", cts.Token);

        // After the reply: a hint that beats it has the client re-read a chat that is
        // not unread yet, and nothing tells it again.
        Assert.Equal(("alice", chat.Id, 1), _notifier.UnreadChanged.Last());
    }
}
