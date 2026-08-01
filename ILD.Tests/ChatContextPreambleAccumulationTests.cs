using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;
using Xunit.Abstractions;

namespace ILD.Tests;

/// <summary>
/// Work item #27: the Chat Context preamble — the ~7.7k-char loop authoring
/// guide included — used to be prepended to EVERY user message, while the chat
/// resumes a persistent provider-side agent session rather than replaying its own
/// transcript. Each turn therefore added another verbatim copy of the static
/// guide to a history the previous copies had never left.
///
/// The fix is a split, and these tests hold both halves of it apart:
///
/// <list type="bullet">
///   <item><b>Once per session</b> — the constant brief and guide go in on the
///   turn that first briefs an agent session and not again, keyed on the session
///   that holds them so a rebound one is briefed afresh. Asserted at the argv a
///   real <see cref="ClaudeCodeAdapter"/> handed the CLI, launch by launch,
///   because that is the channel that was accumulating.</item>
///   <item><b>Still reachable</b> — the half that keeps "sent once" from meaning
///   "seen once": the same bytes are served by <c>get_loop_authoring_guide</c>,
///   and every turn the editor is open names that tool.</item>
///   <item><b>Per turn, still</b> — the guard rails. The open work item id, the
///   worktree path and the editor-open state are per-turn state, and hoisting
///   the static half must not take them with it.</item>
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
    /// This was the repro; it now pins the fix at the same level of evidence —
    /// the argv a real <see cref="ClaudeCodeAdapter"/> handed the CLI process,
    /// launch by launch. The two halves that together made the accumulation are
    /// asserted as a pair, because after the fix they are what makes ONE copy
    /// enough: exactly one launch carries the guide, and every later launch
    /// carries <c>--resume &lt;session&gt;</c>, so that copy is still in front of
    /// the agent on turn 5. Drop the resume half and "sent once" would mean
    /// "seen once".
    /// </summary>
    [Fact]
    public async Task A_loop_editor_conversation_hands_the_cli_exactly_one_copy_of_the_guide()
    {
        const int turns = 5;
        using var cli = new PromptCapturingCli();
        var provider = await SeedProviderAsync(config: cli.ProviderConfigJson);
        var svc = NewService(new ClaudeCodeAdapter());
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        for (var i = 1; i <= turns; i++)
            await svc.ExecuteTurnAsync(started.Id, $"turn {i}", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        Assert.Equal(turns, cli.InvocationCount);

        // Turn 1 briefs the session in full.
        Assert.Contains(GuideMarker, cli.PromptFor(1));
        Assert.Contains(GuideTailMarker, cli.PromptFor(1));

        for (var turn = 2; turn <= turns; turn++)
        {
            var prompt = cli.PromptFor(turn);
            Assert.DoesNotContain(GuideMarker, prompt);
            Assert.DoesNotContain(GuideTailMarker, prompt);
            // The editor being open is per-turn state, so a thin pointer stays —
            // and it names the tool that fetches the guide back on demand.
            Assert.Contains("get_loop_authoring_guide", prompt);
            Assert.EndsWith($"turn {turn}", prompt);
        }

        // The session is resumed, not replayed: turn 1 opens the session and every
        // later turn re-enters it by id, which is exactly why one copy suffices.
        //
        // The first flag is asserted positionally because everything below is a
        // claim about what is ABSENT from an argv, and those are only as honest as
        // the capture: a harness that quietly dropped a leading argument would make
        // "no --resume on turn 1" true for free.
        Assert.Equal("--print", cli.ArgvFor(1)[0]);
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

        var briefed = cli.PromptFor(1).Length;
        var steady = cli.PromptFor(turns).Length;
        _out.WriteLine($"[after] turn 1 {briefed} chars (briefing), turn {turns} {steady} chars (steady state).");
    }

    /// <summary>
    /// Acceptance criterion: "Measure and report before/after input tokens for a
    /// representative multi-turn Loop Editor conversation." Both halves now.
    ///
    /// Method — chars are measured, tokens are estimated. Every AFTER number is
    /// counted from the bytes a real <see cref="ClaudeCodeAdapter"/> handed the
    /// CLI process, so the char counts are exact. Tokens are those chars / 4,
    /// the standard English-prose approximation: no tokenizer ships with this
    /// repo, and chat turns record no usage (<c>ChatService.FinalizeAssistantAsync</c>
    /// drops <c>NodeExecutionResult.Usage</c>), so provider-reported counts are
    /// not available to measure against.
    ///
    /// BEFORE is reconstructed from the same run rather than quoted from the
    /// commit that measured it: the old code sent turn 1's static block on every
    /// turn, so BEFORE is that block times the turn count. Deriving it keeps the
    /// comparison honest as the guide's prose changes — the report cannot drift
    /// into flattering itself the way a hardcoded 83320 would.
    ///
    /// The one assertion is the shape of the curve, not a number: the static
    /// block must be paid once, not once per turn.
    /// </summary>
    [Fact]
    public async Task Measure_input_tokens_before_and_after_for_a_ten_turn_loop_editor_conversation()
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

        // The briefing block: everything turn 1 sends beyond the human's own words.
        // Under the old code this was every turn's preamble, which is what makes it
        // the BEFORE per-turn figure as well as the AFTER one-off.
        var staticBlock = prompts[0].Length - messages[0].Length;

        // Resident: what sits in the agent's context once turn 10 is composed. Every
        // preamble ever sent is still there — the session is resumed, not replayed.
        static long Resident(IEnumerable<string> sent) => sent.Sum(p => (long)p.Length);

        // Billed: an input context is re-read on every turn, so turn k pays for
        // turns 1..k. That is what made the old cost quadratic in the turn count.
        static long Billed(IReadOnlyList<string> sent)
        {
            var total = 0L;
            for (var k = 1; k <= sent.Count; k++) total += Resident(sent.Take(k));
            return total;
        }

        // BEFORE: the same conversation with the static block on every turn.
        var before = messages.Select(m => new string('x', staticBlock) + m).ToArray();

        static long Tok(long chars) => chars / 4;
        var beforeResident = Resident(before);
        var afterResident = Resident(prompts);
        var beforeBilled = Billed(before);
        var afterBilled = Billed(prompts);

        _out.WriteLine("=== 10-turn Loop Editor conversation, editor open throughout ===");
        _out.WriteLine("Method: chars counted from the argv a real ClaudeCodeAdapter handed the CLI; tokens ≈ chars/4.");
        _out.WriteLine("BEFORE is the same run with the static block re-sent per turn, as the old code did.");
        _out.WriteLine($"Static block (once)      : {staticBlock,7} chars  ≈ {Tok(staticBlock),6} tokens");
        _out.WriteLine($"Human text, 10 turns     : {humanChars,7} chars  ≈ {Tok(humanChars),6} tokens");
        _out.WriteLine($"Resident before / after  : {beforeResident,7} / {afterResident,-7} chars  ≈ {Tok(beforeResident),6} / {Tok(afterResident),-6} tokens");
        _out.WriteLine($"Billed  before / after   : {beforeBilled,7} / {afterBilled,-7} chars  ≈ {Tok(beforeBilled),6} / {Tok(afterBilled),-6} tokens (uncached, sum over turns of turns 1..k)");
        _out.WriteLine($"Billed input saved       : {100.0 * (beforeBilled - afterBilled) / beforeBilled:F1}%");
        _out.WriteLine("Note: resident excludes assistant replies and tool traffic, which ILD does not see.");
        for (var i = 0; i < turns; i++)
            _out.WriteLine($"  turn {i + 1,2}: prompt {prompts[i].Length,6} chars (before {before[i].Length,6}), resident {Resident(prompts.Take(i + 1)),7} chars");

        // The curve, not a number: turns 2..10 together must not cost another
        // briefing. Anything that re-sends the static block breaks this whichever
        // way the guide's prose moves.
        var afterTurnOne = Resident(prompts.Skip(1));
        Assert.True(
            afterTurnOne < staticBlock,
            $"turns 2..{turns} cost {afterTurnOne} chars, which is at least one more briefing ({staticBlock} chars) — the static block is still accumulating.");
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

    /// <summary>
    /// Closing and re-opening the editor is navigation, not a new session — the
    /// guide sent on the first open is still in the resumed agent's history, so
    /// re-briefing would just reintroduce the accumulation one open/close cycle at
    /// a time. The per-turn pointer comes back (the editor IS open again); the
    /// guide does not, and the tool is there for an agent that wants it.
    /// </summary>
    [Fact]
    public async Task Reopening_the_loop_editor_later_in_the_same_session_does_not_re_send_the_guide()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "one", "wi-11", OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-11", openLoopDocument: null, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "three", "wi-11", OpenLoopDocument, CancellationToken.None);

        Assert.Contains(GuideMarker, adapter.Prompts[0]);
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[2]);
        Assert.Contains("Loop Editor", adapter.Prompts[2]);
        Assert.Contains("get_loop_authoring_guide", adapter.Prompts[2]);
    }

    /// <summary>
    /// The mirror of the rule above, and the reason delivery is keyed on the bound
    /// session rather than on a "sent it once" flag: a session that is rebound (a
    /// fork, a reset, a snapshot that could not be restored) does not carry the
    /// earlier copy, so it must be briefed again. Keying on the session makes that
    /// automatic — a bare bool would leave the new session permanently unbriefed.
    ///
    /// The re-brief lands on the turn AFTER the rebind, and that is the honest
    /// bound rather than a rounding error: which session a turn binds is only known
    /// once the CLI reports it, which is after the prompt has been built and sent.
    /// One unbriefed turn is recoverable — the agent still has
    /// <c>get_loop_authoring_guide</c> — where a permanently unbriefed session
    /// would not be.
    /// </summary>
    [Fact]
    public async Task An_agent_session_that_is_rebound_is_briefed_again()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "one", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        // From here the CLI hands back a different session id — the old transcript,
        // guide and all, is not what the following turns resume.
        adapter.NextSessionId = "sess-rebound";
        await svc.ExecuteTurnAsync(started.Id, "three", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "four", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "five", openWorkItemId: null, OpenLoopDocument, CancellationToken.None);

        Assert.Contains(GuideMarker, adapter.Prompts[0]);
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[1]);
        // Turn 3 is the turn the rebind happens on; its prompt was already built.
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[2]);
        // Turn 4 is the first one that can see it, and briefs the new session.
        Assert.Contains(GuideMarker, adapter.Prompts[3]);
        Assert.Contains(GuideTailMarker, adapter.Prompts[3]);
        Assert.True(SessionBriefings.IsDelivered(
            _db.Context.ChatSessions.Single().DeliveredBriefings, SessionBriefings.LoopAuthoring, "sess-rebound"));
        // ...after which the rebound session is treated like any other: briefed once.
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[4]);
    }

    /// <summary>
    /// A briefing turn that never reached an agent has not briefed anything, and the
    /// distinction is not academic: the session a turn ENDS on is not the session it
    /// bound. A failed turn keeps the binding it was already resuming, so recording
    /// against that would mark a session briefed by a prompt it never saw — and
    /// because the guide is only ever sent on a mismatch, no later turn would put
    /// that right. The failure is silent and permanent, which is the one shape of
    /// bug the send-once design has to be proof against.
    /// </summary>
    [Fact]
    public async Task A_briefing_turn_that_never_reached_the_agent_does_not_count_as_briefed()
    {
        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        // Turn 1 binds a session, with no editor open — so nothing is briefed yet
        // and the session id is already there to be wrongly recorded against.
        await svc.ExecuteTurnAsync(started.Id, "one", "wi-11", openLoopDocument: null, CancellationToken.None);
        Assert.Equal("sess-1", _db.Context.ChatSessions.Single().CurrentSessionId);

        // Turn 2 opens the editor, so it carries the guide — and the CLI fails to
        // launch, so the agent never sees it.
        adapter.FailNextTurnBeforeLaunch = true;
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-11", OpenLoopDocument, CancellationToken.None);

        var session = _db.Context.ChatSessions.Single();
        Assert.Contains(GuideMarker, adapter.Prompts[1]);
        Assert.False(SessionBriefings.IsDelivered(
            session.DeliveredBriefings, SessionBriefings.LoopAuthoring, session.CurrentSessionId));
        // The binding itself survives the failed turn — that is exactly why the
        // guide flag must not be inferred from it.
        Assert.Equal("sess-1", session.CurrentSessionId);

        // So turn 3 briefs the session for real.
        await svc.ExecuteTurnAsync(started.Id, "three", "wi-11", OpenLoopDocument, CancellationToken.None);
        Assert.Contains(GuideMarker, adapter.Prompts[2]);
        Assert.Contains(GuideTailMarker, adapter.Prompts[2]);
        Assert.True(SessionBriefings.IsDelivered(
            _db.Context.ChatSessions.Single().DeliveredBriefings, SessionBriefings.LoopAuthoring, "sess-1"));

        // ...once.
        await svc.ExecuteTurnAsync(started.Id, "four", "wi-11", OpenLoopDocument, CancellationToken.None);
        Assert.DoesNotContain(GuideMarker, adapter.Prompts[3]);
    }

    // ------------------------------------------------------------------
    // Reachability — the guide left the per-turn channel, not the session.
    // ------------------------------------------------------------------

    /// <summary>
    /// The acceptance criterion the send-once half cannot meet alone: "guide
    /// content remains reachable to the agent for the whole session — do not fix
    /// the cost by making the guidance unreliable." It is met by making the guide
    /// pullable, so this pins the pull path end to end — the same bytes the chat
    /// pushes are served by a tool the agent is told about on every turn the editor
    /// is open.
    ///
    /// The three surfaces are asserted together because LoopTools' own doc comment
    /// names them as the drift risk: the MCP tool, the Pi-side descriptor and the
    /// agent-API route have to agree or the tool exists for one CLI only.
    /// </summary>
    [Fact]
    public void The_authoring_guide_is_pullable_on_demand_from_every_agent_surface()
    {
        var descriptor = Assert.Single(
            ToolDescriptors.All.Where(t => t.Name == "ild_get_loop_authoring_guide"));
        Assert.Equal("api/v1/agent/loop-authoring-guide", descriptor.EndpointPath);
        Assert.Equal(HttpMethod.Get, descriptor.HttpMethod);
        Assert.Empty(descriptor.Parameters);

        // The MCP surface names the same tool.
        var mcpTool = typeof(ILD.McpServer.Tools.LoopTools)
            .GetMethods()
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n =>
                    n.MemberName == "Name" && (string?)n.TypedValue.Value == "get_loop_authoring_guide")));
        Assert.Equal("GetLoopAuthoringGuide", mcpTool.Name);

        // ...and the agent API actually routes it.
        var route = typeof(AgentController)
            .GetMethod(nameof(AgentController.GetLoopAuthoringGuide))!
            .CustomAttributes
            .Single(a => a.AttributeType.Name == "HttpGetAttribute");
        Assert.Equal("loop-authoring-guide", route.ConstructorArguments[0].Value);

        // The pull path and the push path are the same bytes — one constant, so a
        // future edit to the guide cannot leave the two halves disagreeing.
        Assert.Contains(GuideMarker, LoopAuthoringGuide.Text);
        Assert.Contains(GuideTailMarker, LoopAuthoringGuide.Text);
    }

    // ------------------------------------------------------------------
    // The budget — what stops the next static block being inlined here.
    // ------------------------------------------------------------------

    /// <summary>
    /// The guard the loop guide did not have. Every turn's preamble is re-sent into
    /// a resumed session, so anything constant that lands in it costs the turn count
    /// squared — the bug this work item exists for. Once a session is briefed, what
    /// remains should be per-turn state and pointers only, and that is small.
    ///
    /// Deliberately a budget on the whole steady-state preamble rather than a check
    /// for the guide by name: the failure mode is not "someone re-adds the guide",
    /// it is "someone adds the NEXT one" — a work-item authoring primer, a preview
    /// how-to — and a guide-shaped test would not see that coming. The number is
    /// generous against today's ~750 chars with everything open; it is a tripwire
    /// for a block an order of magnitude larger, not a style rule.
    /// </summary>
    [Fact]
    public async Task The_steady_state_preamble_stays_within_its_per_turn_budget()
    {
        const int budgetChars = 1500;

        var adapter = new RecordingChatAdapter();
        var provider = await SeedProviderAsync();
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild", "write" });
        var worktreePath = await SeedActiveRunAsync("wi-99");

        // Everything the Chat Context can carry at once: an open work item, its
        // active-run worktree and grant, and an open Loop Editor.
        await svc.ExecuteTurnAsync(started.Id, "one", "wi-99", OpenLoopDocument, CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "two", "wi-99", OpenLoopDocument, CancellationToken.None);

        const string message = "two";
        var steadyState = adapter.Prompts[1].Length - message.Length - 2; // minus "\n\n"
        _out.WriteLine($"[budget] steady-state preamble {steadyState} chars of {budgetChars} (work item + worktree + editor).");

        // Sanity: the per-turn state really is all still in there, so a preamble
        // that shrank by losing content cannot pass this.
        Assert.Contains("wi-99", adapter.Prompts[1]);
        Assert.Contains(worktreePath, adapter.Prompts[1]);
        Assert.Contains("Loop Editor", adapter.Prompts[1]);

        Assert.True(
            steadyState <= budgetChars,
            $"the per-turn Chat Context preamble is {steadyState} chars, over its {budgetChars}-char budget. "
            + "Every turn re-sends it into a resumed agent session, so constant text here costs the turn "
            + "count squared. If what you added is constant for the session, deliver it once as a "
            + "SessionBriefings briefing and leave a pointer here — see ChatService and work item #27.");
    }

    // ------------------------------------------------------------------
    // The briefing record itself.
    // ------------------------------------------------------------------

    /// <summary>
    /// A briefing belongs to the agent session that received it. Re-recording the
    /// same key must therefore replace the old session's entry rather than append,
    /// or a chat that is rebound repeatedly would grow the column until it hit the
    /// length cap — a smaller version of the accumulation this work item removes.
    /// Other keys are left alone, which is the point of keying at all.
    /// </summary>
    [Fact]
    public void Recording_a_briefing_replaces_its_own_key_and_leaves_others()
    {
        var afterFirst = SessionBriefings.Record(null, SessionBriefings.LoopAuthoring, "sess-1");
        Assert.True(SessionBriefings.IsDelivered(afterFirst, SessionBriefings.LoopAuthoring, "sess-1"));
        Assert.False(SessionBriefings.IsDelivered(afterFirst, SessionBriefings.LoopAuthoring, "sess-2"));

        // A second key coexists...
        var withOther = SessionBriefings.Record(afterFirst, "other-primer", "sess-1");
        Assert.True(SessionBriefings.IsDelivered(withOther, SessionBriefings.LoopAuthoring, "sess-1"));
        Assert.True(SessionBriefings.IsDelivered(withOther, "other-primer", "sess-1"));

        // ...and re-recording one key moves only that key.
        var rebound = SessionBriefings.Record(withOther, SessionBriefings.LoopAuthoring, "sess-2");
        Assert.True(SessionBriefings.IsDelivered(rebound, SessionBriefings.LoopAuthoring, "sess-2"));
        Assert.False(SessionBriefings.IsDelivered(rebound, SessionBriefings.LoopAuthoring, "sess-1"));
        Assert.True(SessionBriefings.IsDelivered(rebound, "other-primer", "sess-1"));
        Assert.Equal(2, rebound.Split('\n').Length);

        // Nothing is delivered into a session that does not exist yet.
        Assert.False(SessionBriefings.IsDelivered(rebound, SessionBriefings.LoopAuthoring, null));
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

        /// <summary>
        /// Set to make this and every later turn bind a DIFFERENT agent session,
        /// as a fork or a snapshot that could not be restored would.
        /// </summary>
        public string? NextSessionId { get; set; }

        /// <summary>
        /// Set to make the next turn throw the way an adapter does when the CLI
        /// never launches — before any session is bound, so nothing this turn was
        /// handed can have reached an agent. Cleared once it has fired.
        /// </summary>
        public bool FailNextTurnBeforeLaunch { get; set; }

        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Contexts.Add(context);
            if (FailNextTurnBeforeLaunch)
            {
                FailNextTurnBeforeLaunch = false;
                throw new InvalidOperationException("claude: command not found");
            }
            // Bind a session on the first turn and hold it, exactly as a real CLI
            // does — this is what makes turn 2 a resume rather than a fresh start.
            return Task.FromResult(
                NodeExecutionResult.Ok("ok", context.Prompt, NextSessionId ?? context.SessionId ?? "sess-1"));
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
