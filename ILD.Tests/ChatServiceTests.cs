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
        public List<bool> Completed { get; } = new();

        public Task MessageAppendedAsync(Guid chatSessionId, ChatMessageView message)
        {
            Appended.Add(new AppendedMessage(chatSessionId, message));
            return Task.CompletedTask;
        }

        public Task TurnProgressAsync(Guid chatSessionId, string delta)
        {
            Progress.Add(delta);
            return Task.CompletedTask;
        }

        public Task TurnCompletedAsync(Guid chatSessionId, bool interrupted)
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

        var view = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" });

        Assert.Equal(provider.Id, view.AiProviderId);
        Assert.Contains("ild", view.Tools);
        var session = _db.Context.ChatSessions.Single();
        Assert.Equal("alice", session.UserId);
        Assert.True(Directory.Exists(session.ScratchPath), "scratch directory should be created");
    }

    [Fact]
    public async Task StartAsync_allows_many_retained_chats_for_the_same_user()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));

        var first = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        var second = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "hi there", CancellationToken.None);

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
        Assert.Single(_notifier.Completed);
        Assert.False(_notifier.Completed[0]);
    }

    [Fact]
    public async Task ExecuteTurnAsync_resumes_the_bound_session_on_the_next_turn()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(ctx =>
            Task.FromResult(NodeExecutionResult.Ok("ok", sessionId: "sess-1")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "first", CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "second", CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "go", cts.Token);

        var assistant = _db.Context.ChatMessages
            .Where(m => m.ChatSessionId == started.Id && m.Role == "assistant")
            .Single();
        Assert.True(assistant.Interrupted);
        Assert.Equal("partial answer", assistant.Content);
        Assert.True(_notifier.Completed[0], "turn-completed should report interrupted");
    }

    [Fact]
    public async Task ExecuteTurnAsync_without_open_work_item_sends_the_raw_message_and_no_extra_dirs()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" });

        await svc.ExecuteTurnAsync(started.Id, "plain message", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "what is open?", "wi-42", openLoopDocument: null, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "write" });
        var worktreePath = await SeedActiveRunAsync("wi-99");

        await svc.ExecuteTurnAsync(started.Id, "edit it", "wi-99", openLoopDocument: null, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        var worktreePath = await SeedActiveRunAsync("wi-99");

        await svc.ExecuteTurnAsync(started.Id, "edit it", "wi-99", openLoopDocument: null, CancellationToken.None);

        Assert.Null(adapter.LastContext!.AdditionalAllowedDirectories);
        Assert.DoesNotContain(worktreePath, adapter.LastContext.Prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_withholds_worktree_for_a_finished_run()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" });
        // A completed run keeps its worktree on disk (ADR-0008) but is not active,
        // so the chat must not expose it (ADR-0011 active-run-only).
        await SeedActiveRunAsync("wi-7", LoopRunStatus.Completed);

        await svc.ExecuteTurnAsync(started.Id, "look", "wi-7", openLoopDocument: null, CancellationToken.None);

        Assert.Null(adapter.LastContext!.AdditionalAllowedDirectories);
    }

    [Fact]
    public async Task ExecuteTurnAsync_stashes_the_open_loop_and_flags_it_without_inlining_the_json()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        const string document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"My Loop\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, "tidy this loop", openWorkItemId: null, document, CancellationToken.None);

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
    public async Task ExecuteTurnAsync_includes_node_variable_and_session_guidance_when_a_loop_is_open()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        const string document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"L\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, "help me wire this up", openWorkItemId: null, document, CancellationToken.None);

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
    }

    [Fact]
    public async Task ExecuteTurnAsync_omits_loop_guidance_when_only_a_work_item_is_open()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "what is open?", "wi-42", openLoopDocument: null, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "hi", CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        const string document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"L\",\"nodes\":[]}";
        await svc.ExecuteTurnAsync(started.Id, "how do loop variables work?", openWorkItemId: null, document, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        // The reported symptom: a human asking about the syntax had their own
        // question rewritten before the agent ever saw it. The angle-bracket
        // form is the shape the report named; both must arrive untouched.
        const string message = "Why do {{WorkItem.Title}}, {{Var.handoff}} and <Foo.Bar> vanish from my prompt?";
        await svc.ExecuteTurnAsync(started.Id, message, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" });

        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        File.WriteAllText(Path.Combine(scratchPath, "notes.txt"), "INLINED-FILE-BODY");

        const string message = "What does {{WorkTree.File:notes.txt}} mean?";
        await svc.ExecuteTurnAsync(started.Id, message, CancellationToken.None);

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        const string first = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"v1\",\"nodes\":[]}";
        const string second = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"v2\",\"nodes\":[]}";

        await svc.ExecuteTurnAsync(started.Id, "first", openWorkItemId: null, first, CancellationToken.None);
        Assert.Equal(first, _loopScratchpad.Get(started.Id));

        // A later message with a new document overwrites the prior snapshot…
        await svc.ExecuteTurnAsync(started.Id, "second", openWorkItemId: null, second, CancellationToken.None);
        Assert.Equal(second, _loopScratchpad.Get(started.Id));

        // …and a message sent with the editor closed clears it so the agent sees no
        // loop, and the preamble no longer mentions the Loop Editor.
        await svc.ExecuteTurnAsync(started.Id, "third", openWorkItemId: null, openLoopDocument: null, CancellationToken.None);
        Assert.Null(_loopScratchpad.Get(started.Id));
        Assert.DoesNotContain("Loop Editor", adapter.LastContext!.Prompt);
    }

    [Fact]
    public async Task ExecuteTurnAsync_names_the_chat_from_the_first_user_message()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        Assert.Null(started.Name);

        await svc.ExecuteTurnAsync(started.Id, "Help me wire up a deploy loop", CancellationToken.None);
        Assert.Equal("Help me wire up a deploy loop", _db.Context.ChatSessions.Single().Name);

        // The name is fixed by the first turn — a later message must not rename it.
        await svc.ExecuteTurnAsync(started.Id, "now add a PR node", CancellationToken.None);
        Assert.Equal("Help me wire up a deploy loop", _db.Context.ChatSessions.Single().Name);
    }

    [Fact]
    public async Task ExecuteTurnAsync_truncates_a_long_first_message_into_the_name()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        var longMessage = new string('a', 200);
        await svc.ExecuteTurnAsync(started.Id, longMessage, CancellationToken.None);

        var name = _db.Context.ChatSessions.Single().Name!;
        Assert.True(name.Length <= 61, "name should be truncated to a sensible length");
        Assert.EndsWith("…", name);
    }

    [Fact]
    public async Task ListForUserAsync_returns_only_the_users_chats_newest_activity_first()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));

        var older = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(older.Id, "first chat", CancellationToken.None);
        var newer = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(newer.Id, "second chat", CancellationToken.None);
        // A different user's chat must never leak into alice's history.
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(bobs.Id, "bob chat", CancellationToken.None);

        // Force the newer chat to have the most recent activity timestamp.
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {DateTime.UtcNow.AddMinutes(-10)} WHERE \"Id\" = {older.Id}");
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {DateTime.UtcNow} WHERE \"Id\" = {newer.Id}");

        var history = await svc.ListForUserAsync("alice");

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
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(started.Id, "hi there", CancellationToken.None);

        var owner = await svc.GetByIdAsync("alice", started.Id);
        Assert.NotNull(owner);
        Assert.Equal(2, owner!.Messages.Count);

        // Another user may not resume alice's chat.
        Assert.Null(await svc.GetByIdAsync("bob", started.Id));
    }

    [Fact]
    public async Task ExistsForUserAsync_is_true_only_for_the_owner()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("ok"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        Assert.True(await svc.ExistsForUserAsync("alice", started.Id));
        // A different user, and a missing id, are both unauthorized/absent.
        Assert.False(await svc.ExistsForUserAsync("bob", started.Id));
        Assert.False(await svc.ExistsForUserAsync("alice", Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAsync_hard_deletes_one_chat_scoped_to_the_owner()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("x"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(started.Id, "hi", CancellationToken.None);

        // Bind a snapshot to the chat session so we can prove the cascade.
        var snapshots = new AdapterSessionSnapshotStore(_db.Context);
        await snapshots.UpsertForChatAsync(started.Id, "fake", "sess-1", "{\"events\":[]}");
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.True(Directory.Exists(scratchPath));

        // A non-owner cannot delete it.
        Assert.False(await svc.DeleteAsync("bob", started.Id));
        Assert.Single(_db.Context.ChatSessions);

        var deleted = await svc.DeleteAsync("alice", started.Id);

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
        var a1 = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        var a2 = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        var bobs = await svc.StartAsync("bob", provider.Id, new[] { "ild" });
        var a1Scratch = _db.Context.ChatSessions.Single(c => c.Id == a1.Id).ScratchPath;

        var removed = await svc.DeleteAllForUserAsync("alice");

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(a1Scratch), "scratch directories should be removed");
        Assert.DoesNotContain(_db.Context.ChatSessions, c => c.Id == a1.Id || c.Id == a2.Id);
        // Bob's chat is untouched.
        Assert.Contains(_db.Context.ChatSessions, c => c.Id == bobs.Id);
    }
}
