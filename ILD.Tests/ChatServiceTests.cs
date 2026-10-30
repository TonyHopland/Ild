using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Analytics;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ChatCloneCoordinator _chatClones = new();
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
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

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
        public Task GlobalVariableProposalsChangedAsync(Guid chatSessionId) => Task.CompletedTask;
        public Task LoopTemplateProposalsChangedAsync(Guid chatSessionId) => Task.CompletedTask;

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
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)).WithDeclaredTraits();

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

    private ChatService NewService(IAgentAdapter adapter, IEgressPolicy? egressPolicy = null)
        => new(_db.Context, _db.Providers, RegistryFor(adapter), _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance, egressPolicy: egressPolicy);

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

    [Theory]
    [InlineData(NetworkMode.Off)]
    [InlineData(NetworkMode.Whitelist)]
    public async Task A_chat_turn_launches_in_the_mode_the_policy_reports(NetworkMode mode)
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok()));
        var svc = NewService(adapter, EgressPolicies.Reporting(mode));

        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hi", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

        Assert.Equal(mode, adapter.LastContext!.NetworkMode);
    }

    [Fact]
    public async Task A_chat_turn_without_a_policy_launches_proxied_as_Monitor()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok()));
        var svc = NewService(adapter);

        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hi", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

        Assert.Equal(NetworkMode.Monitor, adapter.LastContext!.NetworkMode);
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
        // job now: ChatTurnRunnerTests covers it, under the turn's own id.
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
        Assert.Equal(ChatTitleSource.Fallback, ReadSession(started.Id).TitleSource);

        // The name is fixed by the first turn — a later message must not rename it.
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "now add a PR node", CancellationToken.None);
        Assert.Equal("Help me wire up a deploy loop", _db.Context.ChatSessions.Single().Name);
    }

    [Fact]
    public async Task Each_successful_turn_of_an_untitled_chat_hands_a_title_job_off_after_its_reply_is_stored_and_announced_without_waiting_for_it()
    {
        var provider = await SeedProviderAsync();
        var titles = NewTitleScheduler();
        var svc = new ChatService(_db.Context, _db.Providers,
            RegistryFor(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("reply")))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, titles: titles, claudeProjects: TestClaudeProjectFolders.Instance);
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
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, titles: titles, claudeProjects: TestClaudeProjectFolders.Instance);
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
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);
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
        await SessionSnapshotTestSupport.SeedAsync(
            snapshots, SessionSnapshotKey.ForChat(started.Id, "fake", "sess-1"), "raw", "{\"type\":\"user\"}\n", TestContext.Current.CancellationToken);
        Assert.NotEmpty(_db.Context.AdapterSessionSnapshotChunks);
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
        Assert.Empty(_db.Context.AdapterSessionSnapshotChunks);
        Assert.False(Directory.Exists(scratchPath), "scratch directory should be removed");
    }

    [Fact]
    public async Task An_inactive_chat_starred_after_the_sweeps_check_is_kept()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("x"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "hi", CancellationToken.None);
        // Opened up front: a context cannot first open the connection while the sweep's read is still on it.
        using var starring = _db.Fresh();
        Assert.Equal(1, await starring.ChatSessions.CountAsync(TestContext.Current.CancellationToken));
        var starAfterInactiveRead = new StarAfterInactiveRead(starring, started.Id);
        using var deleting = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection()).AddInterceptors(starAfterInactiveRead).Options);
        var sweep = new ChatService(deleting, _db.Providers, Mock.Of<IAgentAdapterRegistry>(), _notifier, Options,
            _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);

        Assert.False(await sweep.DeleteIfInactiveAsync(started.Id, DateTime.UtcNow.AddDays(1), TestContext.Current.CancellationToken));

        Assert.True(starAfterInactiveRead.Starred);
        using var ctx = _db.Fresh();
        var kept = Assert.Single(ctx.ChatSessions.AsNoTracking());
        Assert.True(kept.IsFavorite);
        Assert.NotEmpty(ctx.ChatMessages.AsNoTracking());
    }

    // Stars the chat once the sweep has read it as inactive, before it deletes:
    // starring does not wait for the chat's turn gate.
    private sealed class StarAfterInactiveRead(AppDbContext starring, Guid chatId) : DbCommandInterceptor
    {
        public bool Starred { get; private set; }

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Starred)
            {
                Starred = true;
                await starring.ChatSessions.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsFavorite, true), CancellationToken.None);
            }
            return result;
        }
    }

    [Fact]
    public async Task A_chat_deleted_as_an_analytics_clear_removes_its_usage_rows_is_still_deleted()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("x"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        _db.Context.ChatUsages.Add(new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = started.Id,
            Kind = ChatUsageKind.Turn,
            AiProvider = "Main",
            InputTokens = 5,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clearBeforeSave = new ClearUsageBeforeRemoval();
        using var deleting = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection()).AddInterceptors(clearBeforeSave).Options);
        var request = new ChatService(deleting, _db.Providers, Mock.Of<IAgentAdapterRegistry>(), _notifier, Options,
            _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);

        Assert.True(await request.DeleteAsync("alice", started.Id, TestContext.Current.CancellationToken));

        Assert.True(clearBeforeSave.Cleared);
        using var ctx = _db.Fresh();
        Assert.Empty(ctx.ChatSessions.AsNoTracking());
        Assert.Empty(ctx.ChatUsages.AsNoTracking());
    }

    // An analytics clear commits after the delete read the chat's usage rows and before it removes them.
    private sealed class ClearUsageBeforeRemoval : SaveChangesInterceptor
    {
        public bool Cleared { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Cleared && eventData.Context!.ChangeTracker.Entries<ChatUsage>().Any(e => e.State == EntityState.Deleted))
            {
                Cleared = true;
                await eventData.Context.Set<ChatUsage>().ExecuteDeleteAsync(cancellationToken);
            }
            return result;
        }
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
        Assert.True(await svc.DeleteAsync("alice", two.Id, TestContext.Current.CancellationToken));
        Assert.True(await svc.DeleteAsync("alice", three.Id, TestContext.Current.CancellationToken));
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
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);
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

    [Fact]
    public async Task Messages_that_share_a_sequence_come_back_in_the_order_they_were_saved()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var saved = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        // The later one is stored first, in a save of its own, so row order alone would put it first.
        _db.Context.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), ChatSessionId = chat.Id, Role = "assistant", Content = "later", Sequence = 1, CreatedAt = saved.AddSeconds(1) });
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.Context.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), ChatSessionId = chat.Id, Role = "user", Content = "earlier", Sequence = 1, CreatedAt = saved });
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var view = await svc.GetByIdAsync("alice", chat.Id, TestContext.Current.CancellationToken);

        Assert.Equal(["earlier", "later"], view!.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task A_scheduled_turn_that_fails_records_the_agents_error_without_nul()
    {
        var provider = await SeedProviderAsync();
        var schedules = new Mock<IChatScheduleNotifier>();
        var svc = new ChatService(_db.Context, _db.Providers,
            RegistryFor(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Fail("raw\0 terminal output")))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, schedules: schedules.Object, claudeProjects: TestClaudeProjectFolders.Instance);
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var turnId = Guid.NewGuid();
        var firingId = await SeedRunningFiringAsync(chat.Id, turnId);

        await svc.ExecuteTurnAsync(chat.Id, turnId, "check", CancellationToken.None);

        using var ctx = _db.Fresh();
        var firing = ctx.ChatScheduleFirings.AsNoTracking().Single(f => f.Id == firingId);
        Assert.Equal(ChatScheduleFiringOutcome.Failed, firing.Outcome);
        Assert.Equal("raw terminal output", firing.Reason);
        schedules.Verify(s => s.SchedulesChangedAsync("alice", firing.ScheduleId), Times.Once);
    }

    [Fact]
    public async Task A_restart_note_that_cannot_be_saved_leaves_its_firing_running_and_announces_nothing()
    {
        var provider = await SeedProviderAsync();
        var chat = await NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())))
            .StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var firingId = await SeedRunningFiringAsync(chat.Id, Guid.NewGuid());
        var connection = _db.Context.Database.GetDbConnection();
        var chatNotifier = new Mock<IChatNotifier>();
        var scheduleNotifier = new Mock<IChatScheduleNotifier>();

        ChatScheduleService Recovery(AppDbContext db) => NewRecovery(db, chatNotifier.Object, scheduleNotifier.Object);

        using (var failing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(new FailingNoteSave()).Options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery(failing).RecoverInterruptedAsync(TestContext.Current.CancellationToken));
        }

        using (var ctx = _db.Fresh())
        {
            Assert.Equal(ChatScheduleFiringOutcome.Running, ctx.ChatScheduleFirings.AsNoTracking().Single(f => f.Id == firingId).Outcome);
            Assert.Empty(ctx.ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == chat.Id));
        }
        chatNotifier.VerifyNoOtherCalls();
        scheduleNotifier.VerifyNoOtherCalls();

        // A later recovery that can save writes both.
        using (var working = _db.Fresh())
            await Recovery(working).RecoverInterruptedAsync(TestContext.Current.CancellationToken);
        using (var ctx = _db.Fresh())
        {
            Assert.Equal(ChatScheduleFiringOutcome.Failed, ctx.ChatScheduleFirings.AsNoTracking().Single(f => f.Id == firingId).Outcome);
            Assert.Single(ctx.ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == chat.Id && m.Interrupted));
        }
    }

    [Fact]
    public async Task A_restart_note_is_announced_to_its_open_chat_and_moves_the_chats_last_activity_like_a_reply()
    {
        var provider = await SeedProviderAsync();
        var chat = await NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())))
            .StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var turnId = Guid.NewGuid();
        await SeedRunningFiringAsync(chat.Id, turnId);
        var chatNotifier = new Mock<IChatNotifier>();
        ChatMessage? committed = null;
        DateTime? activityWhenAnnounced = null;
        chatNotifier.Setup(n => n.MessageAppendedAsync(chat.Id, turnId, It.IsAny<ChatMessageView>()))
            .Callback(() =>
            {
                // Announced only once a reader can see the note and the chat's new activity.
                using var ctx = _db.Fresh();
                committed = ctx.ChatMessages.AsNoTracking().SingleOrDefault(m => m.ChatSessionId == chat.Id);
                activityWhenAnnounced = ctx.ChatSessions.AsNoTracking().Single(c => c.Id == chat.Id).UpdatedAt;
            })
            .Returns(Task.CompletedTask);

        using (var db = _db.Fresh())
            await NewRecovery(db, chatNotifier.Object, Mock.Of<IChatScheduleNotifier>()).RecoverInterruptedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(committed);
        Assert.True(committed!.Interrupted);
        Assert.Equal(committed.CreatedAt, activityWhenAnnounced);
        chatNotifier.Verify(n => n.MessageAppendedAsync(chat.Id, turnId, It.Is<ChatMessageView>(v =>
            v.Id == committed.Id && v.Role == "assistant" && v.Interrupted && v.Sequence == committed.Sequence)), Times.Once);
        chatNotifier.Verify(n => n.UnreadChangedAsync("alice", chat.Id), Times.Once);
        chatNotifier.Verify(n => n.ActivityChangedAsync("alice", chat.Id), Times.Once);
    }

    // Through the service's own context, as the turn reads its tracked session.
    private async Task LinkToScheduleAsync(Guid chatId, ChatSchedule schedule)
    {
        _db.Context.ChatSchedules.Add(schedule);
        _db.Context.ChatSessions.Single(c => c.Id == chatId).ScheduleId = schedule.Id;
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<RunAnalyticsOverview> AnalyticsAsync(AnalyticsQuery? query = null)
        => new RunAnalyticsService(_db.Fresh()).GetOverviewAsync(query ?? new AnalyticsQuery(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, "0.07")]
    [InlineData(true, null)]
    public async Task A_successful_turn_that_reports_usage_records_it_once_under_the_chats_provider_name(bool interrupted, string? cost)
    {
        var provider = await SeedProviderAsync();
        var costUsd = cost is null ? (decimal?)null : decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture);
        using var cts = new CancellationTokenSource();
        var svc = NewService(new FakeAdapter(async ctx =>
        {
            await ctx.ProgressCallback!("partial");
            if (interrupted) cts.Cancel();
            return NodeExecutionResult.Ok("reply", usage: new TokenUsage(120, 30, costUsd, AiProvider: "claude-code"));
        }));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", cts.Token);

        using var ctx = _db.Fresh();
        var assistant = ctx.ChatMessages.AsNoTracking().Single(m => m.ChatSessionId == chat.Id && m.Role == "assistant");
        Assert.Equal(interrupted, assistant.Interrupted);
        var usage = Assert.Single(ctx.ChatUsages.AsNoTracking());
        Assert.Equal(chat.Id, usage.ChatSessionId);
        Assert.Equal(assistant.Id, usage.ChatMessageId);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(costUsd, usage.CostUsd);
        Assert.Equal("p1", usage.AiProvider);

        var overview = await AnalyticsAsync();
        Assert.Equal(new ChatAnalytics(null, null, 1, 120, 30, costUsd ?? 0m), Assert.Single(overview.Chats));
        Assert.Equal("p1", Assert.Single(overview.Providers).Provider);
    }

    public enum NothingToRecord { AdapterFails, AdapterThrows, AdapterCancelled, ProviderGone, NoAdapter, NoUsage, ZeroUsage }

    [Theory]
    [InlineData(NothingToRecord.AdapterFails)]
    [InlineData(NothingToRecord.AdapterThrows)]
    [InlineData(NothingToRecord.AdapterCancelled)]
    [InlineData(NothingToRecord.ProviderGone)]
    [InlineData(NothingToRecord.NoAdapter)]
    [InlineData(NothingToRecord.NoUsage)]
    [InlineData(NothingToRecord.ZeroUsage)]
    public async Task A_turn_with_no_usage_to_record_stores_none(NothingToRecord turn)
    {
        var provider = await SeedProviderAsync();
        var usage = new TokenUsage(120, 30, 0.07m);
        var adapter = new FakeAdapter(_ => turn switch
        {
            // A failure carrying usage still records nothing.
            NothingToRecord.AdapterFails => Task.FromResult(new NodeExecutionResult(false, "out", "rate limited", Usage: usage)),
            NothingToRecord.AdapterThrows => throw new InvalidOperationException("crashed"),
            NothingToRecord.AdapterCancelled => throw new OperationCanceledException(),
            NothingToRecord.NoUsage => Task.FromResult(NodeExecutionResult.Ok("reply")),
            NothingToRecord.ZeroUsage => Task.FromResult(NodeExecutionResult.Ok("reply", usage: new TokenUsage(0, 0, 0m))),
            _ => Task.FromResult(NodeExecutionResult.Ok("reply", usage: usage)),
        });
        Func<IAgentAdapter> noAdapter = () => throw new InvalidOperationException("no adapter");
        var registry = turn == NothingToRecord.NoAdapter
            ? Mock.Of<IAgentAdapterRegistry>(r => r.ResolveForProvider(It.IsAny<AiProvider>()) == noAdapter).WithDeclaredTraits()
            : RegistryFor(adapter);
        var svc = new ChatService(_db.Context, _db.Providers, registry, _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        if (turn == NothingToRecord.ProviderGone)
        {
            _db.Context.AiProviders.Remove(provider);
            await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", CancellationToken.None);

        Assert.Single(_db.Fresh().ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == chat.Id && m.Role == "assistant"));
        var overview = await AnalyticsAsync();
        Assert.Empty(overview.Chats);
        Assert.Equal(0, overview.TotalInputTokens);
        Assert.Equal(0, overview.TotalOutputTokens);
        Assert.Equal(0m, overview.TotalCostUsd);
    }

    [Fact]
    public async Task A_scheduled_chats_usage_stays_on_its_schedule_row_after_the_schedule_and_the_chat_are_deleted()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ =>
            Task.FromResult(NodeExecutionResult.Ok("reply", usage: new TokenUsage(100, 10, 0.10m)))));
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            Name = "Nightly check",
            Prompt = "Check.",
            CronExpression = "0 3 * * *",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
        };
        await LinkToScheduleAsync(chat.Id, schedule);

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "first", CancellationToken.None);
        schedule.Name = "Nightly retro";
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // While the schedule exists its row carries the current name.
        Assert.Equal(new ChatAnalytics(schedule.Id, "Nightly retro", 1, 100, 10, 0.10m), Assert.Single((await AnalyticsAsync()).Chats));

        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "second", CancellationToken.None);
        _db.Context.ChatSchedules.Remove(schedule);
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Null(ReadSession(chat.Id).ScheduleId);

        // Gone, it keeps its figures under the last name recorded.
        Assert.Equal(new ChatAnalytics(schedule.Id, "Nightly retro", 2, 200, 20, 0.20m), Assert.Single((await AnalyticsAsync()).Chats));

        // The unlinked chat's next turn is a plain chat's.
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "third", CancellationToken.None);
        var before = await AnalyticsAsync();
        Assert.Equal(new ChatAnalytics(schedule.Id, "Nightly retro", 2, 200, 20, 0.20m), Assert.Single(before.Chats, c => c.ScheduleId == schedule.Id));
        Assert.Equal(new ChatAnalytics(null, null, 1, 100, 10, 0.10m), Assert.Single(before.Chats, c => c.ScheduleId == null));

        Assert.True(await svc.DeleteAsync("alice", chat.Id, TestContext.Current.CancellationToken));

        var after = await AnalyticsAsync();
        Assert.Equal(before.Chats.OrderBy(c => c.ScheduleId), after.Chats.OrderBy(c => c.ScheduleId));
    }

    [Fact]
    public async Task Deleting_chats_keeps_their_figures_in_analytics_counted_once()
    {
        var provider = await SeedProviderAsync();
        var turnUsage = new Queue<TokenUsage>([
            new(100, 10, 0.10m), new(200, 20, 0.20m), new(300, 30, null), new(400, 40, 0.40m), new(1000, 100, 1.00m),
        ]);
        var svc = NewService(new FakeAdapter(_ =>
            Task.FromResult(NodeExecutionResult.Ok("reply", usage: turnUsage.Dequeue()))));
        // Two plain chats and a scheduled one of alice's on the same day and provider, and one of bob's.
        var first = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var second = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var scheduled = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            Name = "Nightly check",
            Prompt = "Check.",
            CronExpression = "0 3 * * *",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
        };
        await LinkToScheduleAsync(scheduled.Id, schedule);
        await svc.ExecuteTurnAsync(first.Id, Guid.NewGuid(), "a", CancellationToken.None);
        await svc.ExecuteTurnAsync(first.Id, Guid.NewGuid(), "b", CancellationToken.None);
        await svc.ExecuteTurnAsync(second.Id, Guid.NewGuid(), "c", CancellationToken.None);
        await svc.ExecuteTurnAsync(scheduled.Id, Guid.NewGuid(), "d", CancellationToken.None);
        await svc.ExecuteTurnAsync(bobs.Id, Guid.NewGuid(), "e", CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var queries = new[]
        {
            new AnalyticsQuery(),
            new AnalyticsQuery(Provider: "P1"),
            new AnalyticsQuery(From: today, To: today, Granularity: AnalyticsGranularity.Month),
            new AnalyticsQuery(From: today.AddDays(1)),
            new AnalyticsQuery(Provider: "someone-else"),
        };
        var before = new List<RunAnalyticsOverview>();
        foreach (var query in queries) before.Add(await AnalyticsAsync(query));
        Assert.Equal(1000 + 1000, before[0].TotalInputTokens);
        Assert.Equal(new ChatAnalytics(null, null, 4, 1600, 160, 1.30m), Assert.Single(before[0].Chats, c => c.ScheduleId == null));

        Assert.True(await svc.DeleteAsync("alice", first.Id, TestContext.Current.CancellationToken));
        Assert.True(await svc.DeleteAsync("alice", second.Id, TestContext.Current.CancellationToken));
        Assert.True(await svc.DeleteAsync("alice", scheduled.Id, TestContext.Current.CancellationToken));
        Assert.Equal([bobs.Id], _db.Fresh().ChatSessions.AsNoTracking().Select(c => c.Id).ToList());

        for (var i = 0; i < queries.Length; i++)
        {
            var after = await AnalyticsAsync(queries[i]);
            Assert.Equal(before[i].TotalRuns, after.TotalRuns);
            Assert.Equal(before[i].TotalInputTokens, after.TotalInputTokens);
            Assert.Equal(before[i].TotalOutputTokens, after.TotalOutputTokens);
            Assert.Equal(before[i].TotalCostUsd, after.TotalCostUsd);
            Assert.Equal(before[i].Providers, after.Providers);
            Assert.Equal(before[i].Series, after.Series);
            Assert.Equal(before[i].Chats.OrderBy(c => c.ScheduleId), after.Chats.OrderBy(c => c.ScheduleId));
        }

        // The rest of the day's usage is still counted live, and archived like the others once deleted.
        Assert.True(await svc.DeleteAsync("bob", bobs.Id, TestContext.Current.CancellationToken));
        var final = await AnalyticsAsync();
        Assert.Equal(before[0].TotalInputTokens, final.TotalInputTokens);
        Assert.Equal(before[0].TotalCostUsd, final.TotalCostUsd);
        Assert.Equal(before[0].Chats.OrderBy(c => c.ScheduleId), final.Chats.OrderBy(c => c.ScheduleId));
    }

    [Fact]
    public async Task Usage_recorded_after_a_chat_delete_has_read_the_chats_usage_is_still_counted_once()
    {
        var provider = await SeedProviderAsync();
        var lateTitle = new RecordTitleBeforeChatDelete(_db.Fresh);
        using var deleting = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection()).AddInterceptors(lateTitle).Options);
        var svc = new ChatService(deleting, _db.Providers, RegistryFor(new FakeAdapter(_ =>
            Task.FromResult(NodeExecutionResult.Ok("reply", usage: new TokenUsage(100, 10, 0.10m))))),
            _notifier, Options, _db.LoopRuns, _loopScratchpad, _chatClones, claudeProjects: TestClaudeProjectFolders.Instance);
        var chat = await svc.StartAsync("alice", provider.Id, new[] { "ild" }, TestContext.Current.CancellationToken);
        await svc.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hi", CancellationToken.None);

        Assert.True(await svc.DeleteAsync("alice", chat.Id, TestContext.Current.CancellationToken));

        Assert.True(lateTitle.Recorded);
        var overview = await AnalyticsAsync();
        Assert.Equal(new ChatAnalytics(null, null, 1, 140, 16, 0.102m), Assert.Single(overview.Chats));
        Assert.Equal(0.102m, overview.TotalCostUsd);
    }

    // A title call lands after the delete read the chat's usage and before the delete commits.
    private sealed class RecordTitleBeforeChatDelete(Func<AppDbContext> context) : SaveChangesInterceptor
    {
        public bool Recorded { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            // The save that archives the chat's usage: the delete has read it and not committed.
            var archived = eventData.Context!.ChangeTracker.Entries<ChatUsage>().FirstOrDefault(e => e.State == EntityState.Deleted);
            if (!Recorded && archived is not null)
            {
                using var other = context();
                // The delete's transaction holds the one shared connection; the title call writes inside it.
                other.Database.UseTransaction(eventData.Context.Database.CurrentTransaction?.GetDbTransaction());
                await ChatUsageLedger.RecordTitleAsync(other, archived.Entity.ChatSessionId, new TokenUsage(40, 6, 0.002m), "p1", CancellationToken.None);
                Recorded = true;
            }
            return result;
        }
    }

    private ChatScheduleService NewRecovery(AppDbContext db, IChatNotifier chatNotifier, IChatScheduleNotifier scheduleNotifier)
        => new(db, _db.Providers, Mock.Of<IChatService>(), Mock.Of<IChatTurnRunner>(), chatNotifier, scheduleNotifier,
            Mock.Of<ISchedulerSettingsService>(), new ChatScheduleLocks(), Mock.Of<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<ChatScheduleService>.Instance);

    private sealed class FailingNoteSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries<ChatMessage>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("the note could not be saved")
                : ValueTask.FromResult(result);
    }

    private async Task<Guid> SeedRunningFiringAsync(Guid chatId, Guid turnId)
    {
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            Name = "Nightly check",
            Prompt = "Check.",
            CronExpression = "0 3 * * *",
            TimeZone = "UTC",
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var firing = new ChatScheduleFiring
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Sequence = 1,
            Trigger = ChatScheduleTrigger.RunNow,
            FiredAt = DateTime.UtcNow,
            ChatSessionId = chatId,
            TurnId = turnId,
            Outcome = ChatScheduleFiringOutcome.Running,
        };
        _db.Context.ChatSchedules.Add(schedule);
        _db.Context.ChatScheduleFirings.Add(firing);
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return firing.Id;
    }
}
