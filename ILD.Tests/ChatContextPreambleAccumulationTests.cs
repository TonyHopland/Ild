using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;
using Xunit.Abstractions;

namespace ILD.Tests;

/// <summary>
/// Work item #27: the Chat Context preamble — the ~7.7k-char loop authoring
/// guide included — is prepended to EVERY user message, while the chat resumes a
/// persistent provider-side agent session rather than replaying its own
/// transcript. Each turn therefore adds another verbatim copy of the static
/// guide to history that the previous copies never left.
///
/// The tests split three ways, and the split is the point:
///
/// <list type="bullet">
///   <item><b>Repro</b> — drives a real <see cref="ClaudeCodeAdapter"/> against a
///   fake <c>claude</c> binary for N turns and asserts on the argv that binary was
///   actually handed. It captures today's behaviour (N copies + <c>--resume</c>),
///   so it passes on main and must be updated by whoever fixes the bug.</item>
///   <item><b>Regression</b> — the one test that FAILS on main:
///   <see cref="Second_turn_with_the_loop_editor_open_does_not_re_send_the_static_guide"/>.
///   It is deliberately neutral between the two candidate fixes: it asks only
///   that the static block leave the per-turn user-message channel, which is
///   true whether the guide moves to a system-prompt slot or is sent once on the
///   editor-open transition.</item>
///   <item><b>Dynamic half</b> — the guard rails. These pass on main and must
///   keep passing: the open work item id, the worktree path and the editor-open
///   state are per-turn state, and hoisting the static half must not take them
///   with it.</item>
/// </list>
/// </summary>
public sealed class ChatContextPreambleAccumulationTests : IDisposable
{
    /// <summary>
    /// The guide's opening line — distinctive enough to count copies with, and
    /// stable across the prose edits the work item explicitly leaves in place.
    /// The guide itself is a private const, so tests address it by content.
    /// </summary>
    private const string GuideMarker = "Loop authoring guide — a loop is a directed graph executed from its Start node.";

    /// <summary>A line from deep inside the guide, so a partial send cannot pass as a whole one.</summary>
    private const string GuideTailMarker = "Hand work across branches with {{Var.<name>}}";

    /// <summary>A representative live loop document, as the editor would post it.</summary>
    private const string OpenLoopDocument =
        "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Deploy\",\"nodes\":[],\"edges\":[]}";

    private readonly ITestOutputHelper _out;
    private readonly TestDb _db = new();
    private readonly ChatLoopScratchpad _loopScratchpad = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-preamble-tests", Guid.NewGuid().ToString("N"));

    public ChatContextPreambleAccumulationTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    // ------------------------------------------------------------------
    // Repro — what the provider process is actually handed, turn by turn.
    // ------------------------------------------------------------------

    /// <summary>
    /// Acceptance criterion 1: "verify empirically that N turns on the Loop
    /// Editor page really do put N copies of the guide in the provider's
    /// context (instrument or capture a request; do not infer)."
    ///
    /// Both halves of that claim are asserted here against a real adapter:
    /// N launches each carry their own copy of the guide, AND every launch after
    /// the first carries <c>--resume &lt;session&gt;</c>, which is what makes the
    /// earlier copies still be there. Either fact alone would be consistent with
    /// a bounded context; together they are the accumulation.
    /// </summary>
    [Fact]
    public async Task Repro_each_turn_of_a_loop_editor_conversation_hands_the_cli_its_own_copy_of_the_guide()
    {
        const int turns = 5;
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var svc = NewService(new ClaudeCodeAdapter());
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        for (var i = 1; i <= turns; i++)
            await svc.ExecuteTurnAsync(started.Id, $"turn {i}", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        Assert.Equal(turns, cli.InvocationCount);

        for (var turn = 1; turn <= turns; turn++)
        {
            var prompt = cli.PromptFor(turn);
            Assert.Contains(GuideMarker, prompt);
            Assert.Contains(GuideTailMarker, prompt);
            Assert.EndsWith($"turn {turn}", prompt);
        }

        // The session is resumed, not replayed: turn 1 opens the session, and
        // every later turn re-enters it by id. So the copy sent on turn 1 is
        // still in the agent's history when turn 5's copy arrives.
        Assert.DoesNotContain("--resume", cli.ArgvFor(1));
        var sessionId = _db.Context.ChatSessions.Single().CurrentSessionId;
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        for (var turn = 2; turn <= turns; turn++)
        {
            var argv = cli.ArgvFor(turn);
            var resumeAt = Array.IndexOf(argv, "--resume");
            Assert.True(resumeAt >= 0, $"turn {turn} did not resume the session");
            Assert.Equal(sessionId, argv[resumeAt + 1]);
        }

        // ILD never replays its own transcript, so the growth is not visible in
        // any one prompt — it is one guide-sized step per turn, provider-side.
        var perTurn = cli.PromptFor(1).Length;
        _out.WriteLine($"[repro] {turns} turns, {perTurn} chars of prompt each, all {turns} resident in one resumed session.");
    }

    /// <summary>
    /// Acceptance criterion: "Measure and report before/after input tokens for a
    /// representative multi-turn Loop Editor conversation." This is the BEFORE
    /// half; the AFTER belongs to whoever implements the fix.
    ///
    /// Method — chars are measured, tokens are estimated. Every number below is
    /// counted from the bytes a real <see cref="ClaudeCodeAdapter"/> handed the
    /// CLI process, so the char counts are exact. Tokens are those chars / 4,
    /// the standard English-prose approximation: no tokenizer ships with this
    /// repo, and chat turns record no usage (<c>ChatService.FinalizeAssistantAsync</c>
    /// drops <c>NodeExecutionResult.Usage</c>), so provider-reported counts are
    /// not available to measure against.
    ///
    /// Asserts nothing about the numbers — it reports them. The behaviour is
    /// pinned by the tests around it.
    /// </summary>
    [Fact]
    public async Task Measure_before_input_tokens_for_a_ten_turn_loop_editor_conversation()
    {
        const int turns = 10;
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var svc = NewService(new ClaudeCodeAdapter());
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        // A representative conversation: the editor stays open throughout (the
        // situation the guide exists to serve) and the human types short asks.
        var messages = new[]
        {
            "help me wire up a deploy loop",
            "add a PR node after the build",
            "route CI failures back to the AI node",
            "what does defaultEdge do here?",
            "rename the second AI node to Reviewer",
            "add a human gate before the merge",
            "why did my edge disappear?",
            "give the Reviewer node a session",
            "add a match rule for TO_REVIEW",
            "is this loop valid to save?",
        };
        Assert.Equal(turns, messages.Length);

        foreach (var message in messages)
            await svc.ExecuteTurnAsync(started.Id, message, openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        var prompts = Enumerable.Range(1, turns).Select(cli.PromptFor).ToArray();
        var humanChars = messages.Sum(m => m.Length);
        var preambleChars = prompts.Sum(p => p.Length) - humanChars;
        var staticPerTurn = prompts[0].Length - messages[0].Length;

        // Resident: what sits in the agent's context once turn 10 is composed.
        // Every preamble ever sent is still there — the session is resumed.
        var residentChars = prompts.Sum(p => p.Length);

        // Billed: an input context is re-read on every turn, so turn k pays for
        // turns 1..k. Absent provider-side prompt caching this is quadratic in
        // the turn count, not linear.
        var billedChars = 0L;
        for (var k = 1; k <= turns; k++)
            billedChars += prompts.Take(k).Sum(p => p.Length);

        static long Tok(long chars) => chars / 4;

        _out.WriteLine("=== BEFORE: 10-turn Loop Editor conversation, editor open throughout ===");
        _out.WriteLine($"Method: chars counted from the argv a real ClaudeCodeAdapter handed the CLI; tokens ≈ chars/4.");
        _out.WriteLine($"Static preamble per turn : {staticPerTurn,7} chars  ≈ {Tok(staticPerTurn),6} tokens");
        _out.WriteLine($"Human text, 10 turns     : {humanChars,7} chars  ≈ {Tok(humanChars),6} tokens");
        _out.WriteLine($"Preamble, 10 turns       : {preambleChars,7} chars  ≈ {Tok(preambleChars),6} tokens  ({turns} copies)");
        _out.WriteLine($"Resident at turn 10      : {residentChars,7} chars  ≈ {Tok(residentChars),6} tokens (user-message channel only)");
        _out.WriteLine($"Billed input, uncached   : {billedChars,7} chars  ≈ {Tok(billedChars),6} tokens (sum over turns of turns 1..k)");
        _out.WriteLine($"Duplication share        : {100.0 * (preambleChars - staticPerTurn) / residentChars:F1}% of the resident conversation is re-sent preamble");
        _out.WriteLine("Note: resident excludes assistant replies and tool traffic, which ILD does not see.");
        for (var i = 0; i < turns; i++)
            _out.WriteLine($"  turn {i + 1,2}: prompt {prompts[i].Length,6} chars, resident {prompts.Take(i + 1).Sum(p => p.Length),7} chars");
    }

    // ------------------------------------------------------------------
    // Regression — the test that fails on main.
    // ------------------------------------------------------------------

    /// <summary>
    /// Acceptance criterion: "Regression test pinning that a second turn in the
    /// same session does not re-send the static block."
    ///
    /// FAILS on main. Turn 1 must still deliver the guide (a fix that just
    /// deletes it is not a fix — the guide must stay reachable), and turn 2 in
    /// the SAME chat session must not carry it again in the user-message
    /// channel. That channel is what accumulates, because the session is
    /// resumed; both candidate fixes take the static text out of it.
    /// </summary>
    [Fact]
    public async Task Second_turn_with_the_loop_editor_open_does_not_re_send_the_static_guide()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "help me wire up a deploy loop", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "now add a PR node", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        // The session really is one session — otherwise "do not re-send" would
        // be trivially satisfied by having started over.
        Assert.Equal(2, adapter.Prompts.Count);
        Assert.NotNull(adapter.Contexts[1].SessionId);
        Assert.Equal(adapter.Contexts[1].SessionId, adapter.Contexts[1].IncomingSessionId);

        Assert.Contains(GuideMarker, adapter.Prompts[0]);
        Assert.Contains(GuideTailMarker, adapter.Prompts[0]);

        Assert.DoesNotContain(GuideMarker, adapter.Prompts[1]);
        Assert.DoesNotContain(GuideTailMarker, adapter.Prompts[1]);

        // The static loop-editor tool instructions ride along with the guide and
        // are just as constant, so they are just as duplicated.
        Assert.DoesNotContain("update_current_loop (full replacement)", adapter.Prompts[1]);

        // The human's own message is untouched by any of this.
        Assert.EndsWith("now add a PR node", adapter.Prompts[1]);
    }

    // ------------------------------------------------------------------
    // Dynamic half — guard rails that must survive the fix.
    // ------------------------------------------------------------------

    /// <summary>
    /// The open work item id changes as the human navigates, so it is per-turn
    /// state and must keep arriving every turn. Passes on main; it exists to
    /// fail if a fix hoists the whole preamble instead of only its static half.
    /// </summary>
    [Fact]
    public async Task Every_turn_still_carries_the_currently_open_work_item_id()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "one", "wi-11", OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-11", OpenLoopDocument, CancellationToken.None);
        // The human opens a different item mid-conversation.
        await svc.ExecuteTurnAsync(started.Id, "three", "wi-22", OpenLoopDocument, CancellationToken.None);

        Assert.Contains("wi-11", adapter.Prompts[0]);
        Assert.Contains("wi-11", adapter.Prompts[1]);
        Assert.Contains("wi-22", adapter.Prompts[2]);
        // A stale id is worse than none: the agent would act on the wrong item.
        Assert.DoesNotContain("wi-11", adapter.Prompts[2]);
    }

    /// <summary>
    /// The active-run worktree path is per-turn state too — and unlike the id it
    /// is also a capability, since it is granted as an extra allowed directory.
    /// It must arrive on every turn, in the prompt and in the grant.
    /// </summary>
    [Fact]
    public async Task Every_turn_still_carries_the_active_run_worktree_path_and_its_grant()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "write" });
        var worktreePath = await SeedActiveRunAsync("wi-99");

        await svc.ExecuteTurnAsync(started.Id, "one", "wi-99", OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-99", OpenLoopDocument, CancellationToken.None);

        foreach (var (prompt, ctx) in adapter.Prompts.Zip(adapter.Contexts))
        {
            Assert.Contains(worktreePath, prompt);
            Assert.Contains(worktreePath, ctx.AdditionalAllowedDirectories!);
        }
    }

    /// <summary>
    /// Editor-open state is per-turn: the human can open the Loop Editor on turn
    /// 3 of a conversation that started without it. The guide has to reach the
    /// agent on that turn — "send once" must mean once per open, not once per
    /// session, or a conversation that opens the editor late never gets it.
    /// </summary>
    [Fact]
    public async Task Opening_the_loop_editor_mid_conversation_delivers_the_guide_on_that_turn()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "one", "wi-11", openLoopDocument: null, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-11", openLoopDocument: null, CancellationToken.None);
        // Turn 3: the human opens the Loop Editor.
        await svc.ExecuteTurnAsync(started.Id, "three", "wi-11", OpenLoopDocument, CancellationToken.None);

        Assert.DoesNotContain(GuideMarker, adapter.Prompts[0]);
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[1]);
        Assert.Contains(GuideMarker, adapter.Prompts[2]);
        Assert.Contains(GuideTailMarker, adapter.Prompts[2]);
    }

    /// <summary>
    /// The mirror case: closing the editor must stop the loop-editor context. A
    /// send-once implementation that tracks "already sent" on the session must
    /// not leave the agent believing an editor is still open.
    /// </summary>
    [Fact]
    public async Task Closing_the_loop_editor_mid_conversation_stops_the_loop_editor_context()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "one", "wi-11", OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-11", openLoopDocument: null, CancellationToken.None);

        Assert.Contains("Loop Editor", adapter.Prompts[0]);
        Assert.DoesNotContain("Loop Editor", adapter.Prompts[1]);
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[1]);
        // The work item half is unaffected by the editor closing.
        Assert.Contains("wi-11", adapter.Prompts[1]);
    }

    // ------------------------------------------------------------------
    // Design-blocking finding, pinned as an assertion.
    // ------------------------------------------------------------------

    /// <summary>
    /// The work item asks whether "the adapter layer exposes a system-prompt slot
    /// the static half can occupy". It does not: the prompt is the only channel
    /// an adapter is given, so a static/dynamic split has to widen this contract
    /// first. Pinned as a test so the answer stops being true silently — the day
    /// someone adds such a slot, this fails and points at the choice.
    /// </summary>
    [Fact]
    public void The_adapter_contract_offers_no_system_prompt_slot_today()
    {
        var carriers = typeof(AgentExecutionContext)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("System", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Instruction", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Preamble", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(carriers);
        Assert.Contains("Prompt", typeof(AgentExecutionContext).GetProperties().Select(p => p.Name));

        // Nor does any CLI adapter reach for a system-prompt flag of its own: the
        // claude launch is built here, in full, from the prompt alone.
        var argv = ClaudeCodeAdapter
            .BuildRunProcessStartInfo("/bin/true", Path.GetTempPath(), "PROMPT", sessionId: "s1")
            .ArgumentList;
        Assert.DoesNotContain(argv, a => a.Contains("system-prompt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PROMPT", argv);
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    /// <summary>
    /// Records every turn, not just the last — the whole subject here is how
    /// turn 2 differs from turn 1.
    /// </summary>
    private sealed class RecordingChatAdapter : IAgentAdapter
    {
        public List<AgentExecutionContext> Contexts { get; } = new();
        public List<string> Prompts => Contexts.Select(c => c.Prompt).ToList();

        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Contexts.Add(context);
            // Bind a session on the first turn and hold it, exactly as a real CLI
            // does — this is what makes turn 2 a resume rather than a fresh start.
            return Task.FromResult(NodeExecutionResult.Ok("ok", context.Prompt, context.SessionId ?? "sess-1"));
        }
    }

    private ChatService NewService(IAgentAdapter adapter)
        => new(
            _db.Context,
            _db.Providers,
            Mock.Of<IAgentAdapterRegistry>(r =>
                r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)),
            new NullChatNotifier(),
            new ChatOptions { ScratchRoot = _scratchRoot },
            _db.LoopRuns,
            _loopScratchpad);

    private sealed class NullChatNotifier : IChatNotifier
    {
        public Task MessageAppendedAsync(Guid chatSessionId, ChatMessageView message) => Task.CompletedTask;
        public Task TurnProgressAsync(Guid chatSessionId, string delta) => Task.CompletedTask;
        public Task TurnCompletedAsync(Guid chatSessionId, bool interrupted) => Task.CompletedTask;
        public Task LoopUpdateRequestedAsync(Guid chatSessionId, string document) => Task.CompletedTask;
    }

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

    private async Task<string> SeedActiveRunAsync(string workItemId)
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
            Status = LoopRunStatus.Running,
            WorktreePath = worktreePath,
            StartedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync();
        return worktreePath;
    }
}
