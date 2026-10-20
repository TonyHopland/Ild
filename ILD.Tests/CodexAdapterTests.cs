using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// The codex adapter against a stand-in <c>codex</c> that records its argv,
/// environment, working directory, stdin, the MCP files it was handed and the
/// session rollouts it can see, then prints a scripted <c>exec --json</c> stream.
/// The event shapes are those of <c>codex-rs/exec/src/exec_events.rs</c> at
/// rust-v0.162.1, and were checked against a real 0.162.1 run (thread.started,
/// an item.completed error item, "Reconnecting..." error events, turn.failed and
/// exit 1, the rollout written as sessions/yyyy/MM/dd/rollout-&lt;stamp&gt;-&lt;id&gt;.jsonl).
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class CodexAdapterTests : IDisposable
{
    private const string Token = "codex-test-token";
    private const string Prompt = "-x a prompt that looks like a flag";
    private const string ProviderMessage = "upstream connect error or disconnect/reset before headers";

    private readonly string _dir = Directory.CreateTempSubdirectory("ild-codex-test-").FullName;
    private readonly string _readRoot;
    private readonly string _home;
    private readonly string _worktree;
    private readonly string _out;
    private readonly string _fakeDll;
    private readonly TestProcessEnvironment _environment;
    private readonly RecordingLogger _logger = new();

    public CodexAdapterTests()
    {
        _readRoot = Directory.CreateDirectory(
            Path.Combine(_dir, "read-root"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        _home = Directory.CreateDirectory(Path.Combine(_dir, "home")).FullName;
        _worktree = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        _out = Path.Combine(_dir, "out");
        _fakeDll = Path.Combine(_dir, "ild-mcp-server.dll");
        File.WriteAllText(_fakeDll, "");

        _environment = new TestProcessEnvironment
        {
            { AgentIsolation.AgentReadRootEnvVar, _readRoot },
            { "ILD_MCP_SERVER_DLL", _fakeDll },
            { "ILD_API_URL", "http://ild.example:5000" },
            { "ILD_API_TOKEN", Token },
            { "HOME", _home },
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- launch ----

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Codex_runs_exec_with_ilds_options_then_the_extra_arguments_then_the_prompt_after_the_separator(string model)
    {
        var script = WriteCodex(Success("ok"));

        var result = await Adapter().ExecuteAsync(Context(script, model: model, extraArgs: "-c model_reasoning_effort=high") with
        {
            AdditionalAllowedDirectories = [" ", _worktree, "/data/worktrees/wi-7", ""],
        });

        Assert.True(result.Success, result.Error);
        var argv = Argv();
        Assert.Equal("exec", argv[0]);
        foreach (var flag in new[] { "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox" })
            Assert.Contains(flag, argv);
        Assert.Equal(new[] { _worktree, "/data/worktrees/wi-7" }, ValuesOf(argv, "--add-dir"));
        if (string.IsNullOrWhiteSpace(model))
            Assert.DoesNotContain("-m", argv);
        else
            Assert.Equal(new[] { model }, ValuesOf(argv, "-m"));

        Assert.Equal(new[] { "--", Prompt }, argv[^2..]);
        Assert.DoesNotContain("resume", argv);
        var firstExtra = Array.IndexOf(argv, "model_reasoning_effort=high") - 1;
        Assert.Equal("-c", argv[firstExtra]);
        Assert.Equal(argv.Length - 3, firstExtra + 1);
        Assert.All(IldOptionIndexes(argv), i => Assert.True(i < firstExtra, $"ILD's {argv[i]} comes after the extra arguments"));

        Assert.Equal(_worktree, File.ReadAllText(Path.Combine(_out, "cwd")).Trim());
        Assert.Equal("closed", File.ReadAllText(Path.Combine(_out, "stdin")).Trim());
    }

    [Fact]
    public async Task A_session_to_continue_is_resumed_after_the_same_exec_options()
    {
        SeedRollout("0199a213-81c0-7800-8aa1-bbab2a035a53", Json(new { type = "session_meta" }));
        var script = WriteCodex(Success("ok", threadId: "0199a213-81c0-7800-8aa1-bbab2a035a53"));

        var result = await Adapter().ExecuteAsync(
            Context(script, model: "gpt-5.5", extraArgs: "-c model_reasoning_effort=high") with
            {
                SessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53",
            });

        Assert.True(result.Success, result.Error);
        var argv = Argv();
        Assert.Equal("exec", argv[0]);
        Assert.Equal(new[] { "resume", "0199a213-81c0-7800-8aa1-bbab2a035a53", "--", Prompt }, argv[^4..]);
        var resume = argv.Length - 4;
        foreach (var flag in new[] { "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--add-dir", "-m" })
            Assert.InRange(Array.IndexOf(argv, flag), 1, resume - 1);
        Assert.InRange(Array.IndexOf(argv, "model_reasoning_effort=high"), 1, resume - 1);
        Assert.Equal(new[] { _worktree }, ValuesOf(argv, "--add-dir"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_worktree_or_under_NoTools_nothing_is_launched(bool noTools)
    {
        var script = WriteCodex(Success("ok"));
        var context = noTools
            ? Context(script) with { NoTools = true }
            : Context(script) with { RunContext = RunContext(Guid.NewGuid(), "/this/does/not/exist") };

        var result = await Adapter().ExecuteAsync(context);

        Assert.False(result.Success);
        Assert.Contains("[codex-error]", result.Error);
        Assert.False(File.Exists(Path.Combine(_out, "argv")), "codex was launched");
    }

    // ---- output ----

    [Fact]
    public async Task The_output_is_the_agent_messages_and_progress_gets_them_and_one_marker_per_started_tool()
    {
        const string threadId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        var script = WriteCodex(Lines(
            ThreadStarted(threadId),
            TurnStarted,
            Item("item.completed", new { id = "item_0", type = "reasoning", text = "Thinking about it" }),
            Item("item.started", new { id = "item_1", type = "command_execution", command = "/bin/bash -lc 'echo hi'", aggregated_output = "", exit_code = (int?)null, status = "in_progress" }),
            Item("item.completed", new { id = "item_1", type = "command_execution", command = "/bin/bash -lc 'echo hi'", aggregated_output = "hi\n", exit_code = 0, status = "completed" }),
            AgentMessage("item_2", "First."),
            Item("item.started", new { id = "item_3", type = "mcp_tool_call", server = "ild", tool = "get_current_loop", arguments = new { }, result = (object?)null, error = (object?)null, status = "in_progress" }),
            Item("item.completed", new { id = "item_3", type = "mcp_tool_call", server = "ild", tool = "get_current_loop", arguments = new { }, result = new { content = Array.Empty<object>(), structured_content = (object?)null }, error = (object?)null, status = "completed" }),
            AgentMessage("item_4", "Second."),
            TurnCompleted(10, 4)));
        var progress = new List<string>();
        var sessionIds = new List<string>();

        var result = await Adapter().ExecuteAsync(Context(script) with
        {
            ProgressCallback = line =>
            {
                lock (progress) progress.Add(line);
                return Task.CompletedTask;
            },
            OnSessionId = sessionIds.Add,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("First.\nSecond.", result.Output);
        Assert.Equal(threadId, result.SessionId);
        Assert.Equal(new[] { threadId }, sessionIds);
        Assert.Contains("First.", progress);
        Assert.Contains("Second.", progress);
        Assert.Single(progress, p => p.Contains("echo hi"));
        Assert.Single(progress, p => p.Contains("get_current_loop"));
        Assert.DoesNotContain(progress, p => p.Contains('\n'));
    }

    [Theory]
    [InlineData("turn.failed", 0)]
    [InlineData("turn.failed", 1)]
    [InlineData("error", 0)]
    public async Task A_failed_turn_fails_the_node_with_the_providers_message_whatever_the_exit_code(string failure, int exitCode)
    {
        // codex 0.162.1 exits 1 after turn.failed; the event, not the exit code, says what went wrong.
        var failureEvent = failure == "turn.failed"
            ? Json(new { type = "turn.failed", error = new { message = ProviderMessage } })
            : Error(ProviderMessage);
        var script = WriteCodex(Lines(ThreadStarted("t-1"), TurnStarted, AgentMessage("item_0", "Halfway."), failureEvent), exitCode);

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.False(result.Success);
        Assert.Contains(ProviderMessage, result.Error);
        Assert.Equal(AiFailureClassifier.ClassifyProviderMessage(ProviderMessage), result.Failure);
        Assert.Equal(FailureKind.Interrupted, result.Failure);
        // ILD is on by default, so its settings file was written; it is gone with the failed turn.
        Assert.Single(WrittenFiles());
        Assert.Empty(ConfigFilesLeft());
    }

    [Fact]
    public async Task Warnings_and_reconnects_before_a_completed_turn_do_not_fail_it()
    {
        var script = WriteCodex(Lines(
            ThreadStarted("t-1"),
            Item("item.completed", new { id = "item_0", type = "error", message = "Model metadata for `x` not found. Defaulting to fallback metadata; this can degrade performance and cause issues." }),
            TurnStarted,
            Error("Reconnecting... 1/5 (stream disconnected before completion)"),
            Error("Reconnecting... 2/5 (stream disconnected before completion)"),
            AgentMessage("item_1", "Done."),
            TurnCompleted(1, 1)));

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.True(result.Success, result.Error);
        Assert.Equal("Done.", result.Output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auth expired")]
    public async Task A_clean_exit_without_assistant_text_fails(string stderr)
    {
        var script = WriteCodex(Lines(ThreadStarted("t-1"), TurnStarted, TurnCompleted(1, 0)), stderr: stderr);

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.False(result.Success);
        Assert.Contains("[codex] no assistant text response", result.Error);
        if (stderr.Length > 0)
            Assert.Contains(stderr, result.Error);
    }

    [Fact]
    public async Task A_non_zero_exit_without_a_failure_event_reports_the_exit_code_and_stderr_and_keeps_the_text_as_output()
    {
        var script = WriteCodex(Lines(ThreadStarted("t-1"), AgentMessage("item_0", "Partial answer.")), exitCode: 3, stderr: "kaput");

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.False(result.Success);
        Assert.StartsWith("exit=3", result.Error);
        Assert.Contains("kaput", result.Error);
        Assert.Contains("Partial answer.", result.Output);
    }

    [Fact]
    public async Task No_nul_reaches_the_output_or_the_error()
    {
        var script = WriteCodex(
            Lines(ThreadStarted("t-1"), AgentMessage("item_0", "with\0nul")),
            exitCode: 2,
            stderrPrintf: "bad\\0stderr");

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.False(result.Success);
        Assert.Contains("withnul", result.Output);
        Assert.DoesNotContain('\0', result.Output!);
        Assert.Contains("badstderr", result.Error);
        Assert.DoesNotContain('\0', result.Error!);
    }

    [Fact]
    public async Task Usage_is_the_last_completed_turns_running_total_and_has_no_cost()
    {
        // codex 0.162.1 reports the session's running total on each turn.completed.
        var script = WriteCodex(Lines(
            ThreadStarted("t-1"),
            TurnStarted,
            AgentMessage("item_0", "One."),
            TurnCompleted(1200, 300, cached: 1000, reasoning: 200),
            TurnStarted,
            AgentMessage("item_1", "Two."),
            TurnCompleted(2000, 350, cached: 1700, reasoning: 210)));

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Usage);
        Assert.Equal(2000, result.Usage!.InputTokens);
        Assert.Equal(350, result.Usage.OutputTokens);
        Assert.Null(result.Usage.CostUsd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resumed_session_reports_only_what_this_run_added_to_its_running_total(bool sameThread)
    {
        // As codex 0.162.1 does: the rollout's last token_count holds the session's
        // total so far, and a resumed turn's turn.completed counts on from it. A
        // thread codex could not resume is started anew and counts from zero.
        const string sessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        var rollout = SeedRollout(sessionId,
            Json(new { type = "session_meta", payload = new { id = sessionId } }),
            TaskStarted,
            TokenCount(100, 20),
            Json(new { type = "response_item", payload = new { type = "message" } }),
            TaskStarted,
            TokenCount(250, 45));
        var script = WriteCodex(Lines(
                ThreadStarted(sameThread ? sessionId : "0199a213-81c0-7800-8aa1-0000000000ff"),
                TurnStarted,
                AgentMessage("item_0", "Again."),
                TurnCompleted(sameThread ? 400 : 150, sameThread ? 60 : 15)),
            appendRollout: sameThread ? (rollout, Lines(TaskStarted, TokenCount(400, 60))) : null);

        var result = await Adapter().ExecuteAsync(Context(script) with { SessionId = sessionId });

        Assert.True(result.Success, result.Error);
        Assert.Equal(150, result.Usage!.InputTokens);
        Assert.Equal(15, result.Usage.OutputTokens);
        Assert.Null(result.Usage.CostUsd);
    }

    [Fact]
    public async Task A_resumed_turn_whose_total_is_below_the_sessions_earlier_one_reports_no_usage()
    {
        const string sessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        var rollout = SeedRollout(sessionId, TaskStarted, TokenCount(250, 45));
        var script = WriteCodex(
            Lines(ThreadStarted(sessionId), TurnStarted, AgentMessage("item_0", "Again."), TurnCompleted(150, 15)),
            appendRollout: (rollout, Lines(TaskStarted, TokenCount(150, 15))));

        var result = await Adapter().ExecuteAsync(Context(script) with { SessionId = sessionId });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Without_a_completed_turn_there_is_no_usage()
    {
        var script = WriteCodex(Lines(ThreadStarted("t-1"), AgentMessage("item_0", "Answer.")));

        var result = await Adapter().ExecuteAsync(Context(script));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Cancelling_kills_codex_and_removes_its_mcp_files()
    {
        var script = WriteCodex(ThreadStarted("t-1"), hang: true);
        using var cts = new CancellationTokenSource();

        var result = await Adapter().ExecuteAsync(Context(script, customMcpServersJson: DocsServer) with
        {
            Cancel = cts.Token,
            OnSessionId = _ => cts.Cancel(),
        });

        Assert.False(result.Success);
        Assert.Contains("codex", result.Error);
        Assert.Contains("timed out", result.Error);
        Assert.DoesNotContain("could not be killed", result.Error);
        var pid = int.Parse(File.ReadAllText(Path.Combine(_out, "pid")).Trim());
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "codex is still running");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        Assert.Equal(2, WrittenFiles().Length);
        Assert.Empty(ConfigFilesLeft());
    }

    // ---- MCP servers ----

    private const string DocsServer = """
        {
          "docs": { "command": ["npx", "-y", "docs-mcp"], "env": { "DOCS_TOKEN": "docs-secret", "NO_PROXY": "docs.example.com" } },
          "bad.name": { "command": ["evil-mcp"] }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ild_and_each_custom_server_reach_codex_only_through_files_it_can_read_and_not_change(bool chat)
    {
        _environment.Set(AgentIsolation.EgressProxyPortEnvVar, "3128");
        var ownerId = Guid.NewGuid();
        var provider = Provider(WriteCodex(Success("ok")), customMcpServersJson: DocsServer);

        var result = await Adapter().ExecuteAsync(Context(provider) with
        {
            RunContext = RunContext(ownerId, _worktree),
            ChatSessionId = chat ? ownerId : null,
        });

        Assert.True(result.Success, result.Error);
        var argv = Argv();

        // ild: dotnet <dll>, told only where its settings file is.
        var ild = McpOverride(argv, "ild");
        Assert.Contains("ILD_MCP_ENV_FILE", ild);
        var ildStrings = TomlStrings(ild);
        Assert.Equal("dotnet", ildStrings[0]);
        Assert.Equal(Path.GetFullPath(_fakeDll), ildStrings[1]);
        using (var settings = JsonDocument.Parse(ReadCopy(ildStrings[^1])))
        {
            var root = settings.RootElement;
            Assert.Equal("http://ild.example:5000", root.GetProperty("ILD_API_URL").GetString());
            Assert.Equal(Token, root.GetProperty("ILD_API_TOKEN").GetString());
            if (chat)
            {
                Assert.Equal(ownerId.ToString(), root.GetProperty("ILD_CHAT_SESSION_ID").GetString());
                Assert.False(root.TryGetProperty("ILD_LOOP_RUN_ID", out _));
            }
            else
            {
                Assert.Equal(ownerId.ToString(), root.GetProperty("ILD_LOOP_RUN_ID").GetString());
                Assert.False(root.TryGetProperty("ILD_CHAT_SESSION_ID", out _));
            }
        }

        // docs: dotnet <dll> --launch <file>; the file holds the real server and the proxy.
        var docsStrings = TomlStrings(McpOverride(argv, "docs"));
        Assert.Equal(new[] { "dotnet", Path.GetFullPath(_fakeDll), "--launch" }, docsStrings[..3]);
        Assert.Equal(4, docsStrings.Length);
        using (var launch = JsonDocument.Parse(ReadCopy(docsStrings[3])))
        {
            var root = launch.RootElement;
            Assert.Equal("npx", root.GetProperty("command").GetString());
            Assert.Equal(new[] { "-y", "docs-mcp" }, root.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
            var env = root.GetProperty("env");
            var proxy = AgentIsolation.ResolveEgressProxyUrl("3128", provider.Id)!;
            Assert.Equal("docs-secret", env.GetProperty("DOCS_TOKEN").GetString());
            Assert.Equal(proxy, env.GetProperty("HTTP_PROXY").GetString());
            Assert.Equal(proxy, env.GetProperty("https_proxy").GetString());
            Assert.Equal("docs.example.com", env.GetProperty("NO_PROXY").GetString());
        }

        // A name codex cannot take as a -c key is skipped, and the log says which.
        Assert.DoesNotContain(argv, a => a.Contains("bad.name") || a.Contains("evil-mcp"));
        Assert.Contains(_logger.Warnings, w => w.Contains("bad.name"));

        // Secrets stay in the files: never in codex's argv or environment.
        var environ = File.ReadAllText(Path.Combine(_out, "env"));
        foreach (var secret in new[] { Token, "docs-secret", "docs-mcp" })
        {
            Assert.DoesNotContain(argv, a => a.Contains(secret));
            Assert.DoesNotContain(secret, environ);
        }

        // Readable by the agent group, changeable by the orchestrator only; named for the run or chat.
        var written = WrittenFiles();
        Assert.Equal(new[] { ildStrings[^1], docsStrings[3] }.Order(), written.Select(w => w.Path).Order());
        foreach (var (path, mode, owner) in written)
        {
            Assert.Equal("640", mode);
            Assert.Equal(Environment.UserName, owner);
            Assert.Contains($"-{ownerId:N}-", Path.GetFileName(path));
        }

        // Gone after the turn, and where the run's cleanup and the startup sweep look.
        Assert.Empty(ConfigFilesLeft());
        foreach (var (path, _, _) in written)
            File.WriteAllText(path, "{}");
        var cutoff = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(written.Select(w => w.Path).Order(), IldMcpServer.StaleConfigFiles(_readRoot, cutoff).Order());
        IldMcpServer.DeleteConfigFiles(ownerId, _environment);
        Assert.All(written, w => Assert.False(File.Exists(w.Path)));
    }

    public static TheoryData<string[]> IldOff => new() { Array.Empty<string>(), new[] { "read" } };

    [Theory]
    [MemberData(nameof(IldOff))]
    public async Task With_ild_off_custom_servers_still_run_and_ild_does_not(string[] allowlist)
    {
        var script = WriteCodex(Success("ok"));

        var result = await Adapter().ExecuteAsync(Context(script, customMcpServersJson: DocsServer) with { ToolAllowlist = allowlist });

        Assert.True(result.Success, result.Error);
        var argv = Argv();
        Assert.DoesNotContain(argv, a => a.StartsWith("mcp_servers.ild", StringComparison.Ordinal));
        var launchFile = TomlStrings(McpOverride(argv, "docs"))[3];
        Assert.Equal(new[] { launchFile }, WrittenFiles().Select(w => w.Path));
        // No proxy configured, so none is set for the server.
        using var launch = JsonDocument.Parse(ReadCopy(launchFile));
        Assert.False(launch.RootElement.GetProperty("env").TryGetProperty("HTTP_PROXY", out _));
        Assert.Empty(ConfigFilesLeft());
    }

    [Fact]
    public async Task With_ild_off_and_no_custom_servers_codex_gets_no_mcp_server_and_no_file()
    {
        var script = WriteCodex(Success("ok"));

        var result = await Adapter().ExecuteAsync(Context(script) with { ToolAllowlist = [] });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(Argv(), a => a.StartsWith("mcp_servers.", StringComparison.Ordinal));
        Assert.Empty(WrittenFiles());
    }

    // ---- sessions (ADR-0009) ----

    [Fact]
    public async Task A_managed_turn_stores_the_threads_rollout()
    {
        const string threadId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        const string rollout = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + threadId + "\"}}\n{\"type\":\"response_item\"}\n";
        using var store = new SnapshotDb();
        var runId = Guid.NewGuid();
        await store.AddRunAsync(runId);
        var script = WriteCodex(Success("ok", threadId: threadId),
            writeRollout: ($"2026/10/14/rollout-2026-10-14T10-00-00-{threadId}.jsonl", rollout));

        var result = await Adapter(store).ExecuteAsync(Context(script) with
        {
            RunContext = RunContext(runId, _worktree),
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(threadId, result.SessionId);
        Assert.Equal(rollout, await store.GetAsync(runId, threadId));
        Assert.Equal("codex-jsonl", (await store.MetadataAsync(runId, threadId))?.Format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_rollout_is_restored_under_a_name_codex_resumes_and_an_existing_one_is_left_alone(bool rolloutExists)
    {
        const string sessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        var snapshot = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + sessionId + "\"}}\n";
        using var store = new SnapshotDb();
        var runId = Guid.NewGuid();
        await store.SeedAsync(runId, sessionId, snapshot);
        var existing = Path.Combine(_home, ".codex", "sessions", "2026", "09", "01", $"rollout-2026-09-01T08-00-00-{sessionId}.jsonl");
        if (rolloutExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            File.WriteAllText(existing, "local copy\n");
        }
        var script = WriteCodex(Success("ok", threadId: sessionId));

        var result = await Adapter(store).ExecuteAsync(Context(script) with
        {
            RunContext = RunContext(runId, _worktree),
            SessionId = sessionId,
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        var seen = Assert.Single(RolloutsSeenByCodex());
        if (rolloutExists)
        {
            Assert.Equal(existing, seen);
            Assert.Equal("local copy\n", File.ReadAllText(existing));
        }
        else
        {
            Assert.StartsWith(Path.Combine(_home, ".codex", "sessions") + "/", seen);
            Assert.Matches($"^rollout-\\d{{4}}-\\d{{2}}-\\d{{2}}T\\d{{2}}-\\d{{2}}-\\d{{2}}-{sessionId}\\.jsonl$", Path.GetFileName(seen));
            Assert.Equal(snapshot, File.ReadAllText(seen));
        }
        Assert.Contains("resume", Argv());
    }

    [Fact]
    public async Task A_fork_resumes_on_a_copy_under_the_new_id_and_leaves_the_source_alone()
    {
        const string source = "0199a213-81c0-7800-8aa1-000000000001";
        const string fork = "0199a213-81c0-7800-8aa1-000000000002";
        var sourceRollout = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + source + "\"}}\n";
        using var store = new SnapshotDb();
        var runId = Guid.NewGuid();
        await store.SeedAsync(runId, source, sourceRollout);
        var script = WriteCodex(Success("ok", threadId: fork));

        var result = await Adapter(store).ExecuteAsync(Context(script) with
        {
            RunContext = RunContext(runId, _worktree),
            SessionId = fork,
            ForkFromSessionId = source,
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(sourceRollout, await store.GetAsync(runId, source));
        var restored = Assert.Single(RolloutsSeenByCodex());
        Assert.EndsWith($"-{fork}.jsonl", restored);
        var copy = File.ReadAllText(restored);
        Assert.Contains(fork, copy);
        Assert.DoesNotContain(source, copy);
        Assert.Equal(new[] { "resume", fork }, Argv()[^4..^2]);
    }

    [Fact]
    public async Task A_failing_snapshot_store_does_not_fail_the_run()
    {
        var script = WriteCodex(Success("ok", threadId: "0199a213-81c0-7800-8aa1-bbab2a035a53"),
            writeRollout: ("2026/10/14/rollout-2026-10-14T10-00-00-0199a213-81c0-7800-8aa1-bbab2a035a53.jsonl", "{}\n"));

        using var broken = new SnapshotDb(broken: true);
        var result = await Adapter(broken).ExecuteAsync(Context(script) with
        {
            SessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53",
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public async Task A_fork_the_snapshot_store_cannot_make_fails_the_node_and_launches_nothing()
    {
        var script = WriteCodex(Success("ok", threadId: "0199a213-81c0-7800-8aa1-000000000002"));

        using var broken = new SnapshotDb(broken: true);
        var result = await Adapter(broken).ExecuteAsync(Context(script) with
        {
            SessionId = "0199a213-81c0-7800-8aa1-000000000002",
            ForkFromSessionId = "0199a213-81c0-7800-8aa1-000000000001",
            ManageSession = true,
        });

        Assert.False(result.Success);
        Assert.StartsWith("[codex-error]", result.Error);
        Assert.Contains("fork", result.Error);
        Assert.False(File.Exists(Path.Combine(_out, "argv")), "codex was launched");
        Assert.Empty(ConfigFilesLeft());
    }

    [Fact]
    public async Task A_fork_whose_source_has_no_snapshot_starts_a_new_thread()
    {
        const string fork = "0199a213-81c0-7800-8aa1-000000000002";
        const string fresh = "0199a213-81c0-7800-8aa1-0000000000aa";
        var script = WriteCodex(Success("ok", threadId: fresh));

        using var store = new SnapshotDb();
        var result = await Adapter(store).ExecuteAsync(Context(script) with
        {
            SessionId = fork,
            ForkFromSessionId = "0199a213-81c0-7800-8aa1-000000000001",
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("resume", Argv());
        Assert.Equal(fresh, result.SessionId);
        Assert.Contains(_logger.Warnings, w => w.Contains(fork));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_with_no_rollout_and_no_snapshot_starts_a_new_thread_instead_of_resuming(bool managed)
    {
        const string sessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        const string fresh = "0199a213-81c0-7800-8aa1-0000000000bb";
        var script = WriteCodex(Success("ok", threadId: fresh));

        using var store = new SnapshotDb();
        var result = await Adapter(managed ? store : null).ExecuteAsync(Context(script) with
        {
            SessionId = sessionId,
            ManageSession = managed,
        });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("resume", Argv());
        Assert.DoesNotContain(sessionId, Argv());
        Assert.Equal(fresh, result.SessionId);
        Assert.Contains(_logger.Warnings, w => w.Contains(sessionId));
    }

    [Fact]
    public async Task A_turn_that_extends_the_rollout_stores_only_what_it_added_and_a_lost_rollout_comes_back_byte_identical()
    {
        const string threadId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        using var store = new SnapshotDb();
        var runId = Guid.NewGuid();
        await store.AddRunAsync(runId);
        var rollout = Path.Combine(_home, ".codex", "sessions", "2026", "10", "14", $"rollout-2026-10-14T10-00-00-{threadId}.jsonl");
        var first = await Adapter(store).ExecuteAsync(Context(WriteCodex(Success("ok", threadId: threadId),
            writeRollout: ($"2026/10/14/rollout-2026-10-14T10-00-00-{threadId}.jsonl",
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + threadId + "\"}}\n"))) with
        {
            RunContext = RunContext(runId, _worktree),
            ManageSession = true,
        });
        Assert.True(first.Success, first.Error);
        var afterFirst = await store.MetadataAsync(runId, threadId);

        var second = await Adapter(store).ExecuteAsync(Context(WriteCodex(Success("ok", threadId: threadId),
            appendRollout: (rollout, Lines(TaskStarted, TokenCount(150, 15))))) with
        {
            RunContext = RunContext(runId, _worktree),
            SessionId = threadId,
            ManageSession = true,
        });
        Assert.True(second.Success, second.Error);
        var afterSecond = await store.MetadataAsync(runId, threadId);
        var whole = File.ReadAllBytes(rollout);

        Assert.NotNull(afterFirst);
        Assert.NotNull(afterSecond);
        Assert.Equal("codex-jsonl", afterSecond!.Format);
        Assert.Equal(afterFirst!.ChunkCount + 1, afterSecond.ChunkCount);
        Assert.Equal(whole.Length, afterSecond.ByteLength);

        File.Delete(rollout);
        var third = await Adapter(store).ExecuteAsync(Context(WriteCodex(Success("ok", threadId: threadId))) with
        {
            RunContext = RunContext(runId, _worktree),
            SessionId = threadId,
            ManageSession = true,
        });

        Assert.True(third.Success, third.Error);
        Assert.Equal(whole, File.ReadAllBytes(Assert.Single(RolloutsSeenByCodex())));
    }

    [Fact]
    public async Task A_stored_snapshot_of_another_format_is_never_restored_as_a_rollout()
    {
        const string sessionId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
        const string fresh = "0199a213-81c0-7800-8aa1-0000000000cc";
        using var store = new SnapshotDb();
        var runId = Guid.NewGuid();
        await store.SeedAsync(runId, sessionId, "{\"format\":\"claude-jsonl\",\"events\":[]}", format: "raw");

        var result = await Adapter(store).ExecuteAsync(Context(WriteCodex(Success("ok", threadId: fresh))) with
        {
            RunContext = RunContext(runId, _worktree),
            SessionId = sessionId,
            ManageSession = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Empty(RolloutsSeenByCodex());
        Assert.DoesNotContain("resume", Argv());
    }

    // ---- helpers ----

    private CodexAdapter Adapter(SnapshotDb? store = null)
    {
        if (store is null)
            return new CodexAdapter(_logger, _environment);
        var services = new ServiceCollection();
        store.Register(services);
        var provider = services.BuildServiceProvider();
        return new CodexAdapter(provider.GetRequiredService<IServiceScopeFactory>(), _logger, _environment);
    }

    private AiProvider Provider(string binaryPath, string model = "", string? extraArgs = null, string? customMcpServersJson = null)
    {
        var config = new Dictionary<string, object?> { ["binaryPath"] = binaryPath };
        if (extraArgs is not null) config["extraArgs"] = extraArgs;
        if (customMcpServersJson is not null) config["customMcpServersJson"] = customMcpServersJson;
        return new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "codex-test",
            Type = "codex",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = model,
            Config = JsonSerializer.Serialize(config),
        };
    }

    private AgentExecutionContext Context(string binaryPath, string model = "", string? extraArgs = null, string? customMcpServersJson = null)
        => Context(Provider(binaryPath, model, extraArgs, customMcpServersJson));

    private AgentExecutionContext Context(AiProvider provider)
        => new(
            Provider: provider,
            Prompt: Prompt,
            RunContext: RunContext(Guid.NewGuid(), _worktree),
            ExecutionCount: 1,
            Cancel: TestContext.Current.CancellationToken);

    private static LoopRunContext RunContext(Guid runId, string worktreePath)
        => new(runId, "wi", "t", "d", worktreePath, "main", new List<string>(), null);

    /// <summary>
    /// A stand-in codex. It records what it was given into <see cref="_out"/>,
    /// drains stdin the way <c>codex exec</c> does (reading it to the end before
    /// starting; the guard only bounds a stdin that is never closed), copies the
    /// MCP files then present in the agent read root and lists the rollouts it can
    /// resume, optionally writes its own rollout, prints <paramref name="stdout"/>
    /// and exits.
    /// </summary>
    private string WriteCodex(
        string stdout, int exitCode = 0, string stderr = "", string? stderrPrintf = null, bool hang = false,
        (string RelativePath, string Content)? writeRollout = null, (string Path, string Content)? appendRollout = null)
    {
        Directory.CreateDirectory(_out);
        var configDir = Path.Combine(_readRoot, "ild-mcp-config");
        var sessions = Path.Combine(_home, ".codex", "sessions");
        var script = Path.Combine(_dir, $"codex-{Guid.NewGuid():N}.sh");
        var body =
            "#!/bin/sh\n" +
            $"out='{_out}'\n" +
            "printf '%s\\0' \"$@\" > \"$out/argv\"\n" +
            "env > \"$out/env\"\n" +
            "pwd > \"$out/cwd\"\n" +
            "echo $$ > \"$out/pid\"\n" +
            "if timeout 20 cat > /dev/null; then echo closed > \"$out/stdin\"; else echo open > \"$out/stdin\"; fi\n" +
            "mkdir -p \"$out/files\"\n" +
            ": > \"$out/written\"\n" +
            $"for f in '{configDir}'/*; do\n" +
            "  [ -f \"$f\" ] || continue\n" +
            "  cp \"$f\" \"$out/files/\"\n" +
            "  stat -c '%n %a %U' \"$f\" >> \"$out/written\"\n" +
            "done\n" +
            $"find '{sessions}' -type f -name 'rollout-*.jsonl' > \"$out/rollouts\" 2>/dev/null\n";
        if (writeRollout is { } rollout)
        {
            var target = Path.Combine(sessions, rollout.RelativePath);
            body +=
                $"mkdir -p '{Path.GetDirectoryName(target)}'\n" +
                $"cat > '{target}' <<'ROLLOUT'\n{rollout.Content}ROLLOUT\n";
        }
        if (appendRollout is { } append)
            body += $"cat >> '{append.Path}' <<'ROLLOUT'\n{append.Content}\nROLLOUT\n";
        body += $"cat <<'EVENTS'\n{stdout}\nEVENTS\n";
        if (stderr.Length > 0)
            body += $"printf '%s' '{stderr}' >&2\n";
        if (stderrPrintf is not null)
            body += $"printf '{stderrPrintf}' >&2\n";
        body += hang ? "exec sleep 600\n" : $"exit {exitCode}\n";
        File.WriteAllText(script, body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>A rollout for <paramref name="sessionId"/> under codex's sessions directory, as an earlier run left it.</summary>
    private string SeedRollout(string sessionId, params string[] lines)
    {
        var rollout = Path.Combine(_home, ".codex", "sessions", "2026", "09", "01", $"rollout-2026-09-01T08-00-00-{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        File.WriteAllLines(rollout, lines);
        return rollout;
    }

    private string[] Argv()
    {
        var raw = File.ReadAllText(Path.Combine(_out, "argv"));
        return raw.Split('\0')[..^1];
    }

    private (string Path, string Mode, string Owner)[] WrittenFiles()
    {
        var file = Path.Combine(_out, "written");
        return File.Exists(file)
            ? File.ReadAllLines(file).Where(l => l.Length > 0).Select(l =>
            {
                var parts = l.Split(' ');
                return (parts[0], parts[1], parts[2]);
            }).ToArray()
            : [];
    }

    private string[] ConfigFilesLeft()
    {
        var configDir = Path.Combine(_readRoot, "ild-mcp-config");
        return Directory.Exists(configDir) ? Directory.GetFiles(configDir) : [];
    }

    private string[] RolloutsSeenByCodex()
        => File.ReadAllLines(Path.Combine(_out, "rollouts")).Where(l => l.Length > 0).ToArray();

    private string ReadCopy(string writtenPath)
        => File.ReadAllText(Path.Combine(_out, "files", Path.GetFileName(writtenPath)));

    private static string[] ValuesOf(string[] argv, string flag)
        => argv.Select((a, i) => (a, i)).Where(p => p.a == flag).Select(p => argv[p.i + 1]).ToArray();

    /// <summary>Indexes of every option ILD itself sets: its flags, their values' flags, and its MCP overrides.</summary>
    private static IEnumerable<int> IldOptionIndexes(string[] argv)
    {
        string[] flags = ["--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--add-dir", "-m"];
        for (var i = 0; i < argv.Length; i++)
        {
            if (flags.Contains(argv[i])
                || (argv[i] == "-c" && i + 1 < argv.Length && argv[i + 1].StartsWith("mcp_servers.", StringComparison.Ordinal)))
                yield return i;
        }
    }

    /// <summary>The value of the <c>-c mcp_servers.&lt;name&gt;=…</c> override, after the <c>=</c>.</summary>
    private static string McpOverride(string[] argv, string name)
    {
        var prefix = $"mcp_servers.{name}=";
        var matches = argv.Where((a, i) => i > 0 && argv[i - 1] == "-c" && a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return Assert.Single(matches)[prefix.Length..];
    }

    /// <summary>The TOML basic strings of an inline table, in order.</summary>
    private static string[] TomlStrings(string value)
        => Regex.Matches(value, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => Regex.Unescape(m.Groups[1].Value)).ToArray();

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string ThreadStarted(string threadId) => Json(new { type = "thread.started", thread_id = threadId });

    private static readonly string TurnStarted = Json(new { type = "turn.started" });

    private static string Item(string type, object item) => Json(new { type, item });

    private static string AgentMessage(string id, string text) => Item("item.completed", new { id, type = "agent_message", text });

    private static string Error(string message) => Json(new { type = "error", message });

    private static string TurnCompleted(long input, long output, long cached = 0, long reasoning = 0)
        => Json(new
        {
            type = "turn.completed",
            usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = reasoning },
        });

    private static readonly string TaskStarted = Json(new { type = "event_msg", payload = new { type = "task_started" } });

    private static string TokenCount(long input, long output)
        => Json(new
        {
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { total_token_usage = new { input_tokens = input, cached_input_tokens = 0, output_tokens = output, reasoning_output_tokens = 0, total_tokens = input + output } },
            },
        });

    private static string Success(string text, string threadId = "0199a213-81c0-7800-8aa1-bbab2a035a53")
        => Lines(ThreadStarted(threadId), TurnStarted, AgentMessage("item_0", text), TurnCompleted(5, 1));

    private sealed class RecordingLogger : ILogger<CodexAdapter>
    {
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// The real snapshot store on an in-memory database. A broken one has no schema,
    /// so every call the adapter makes fails.
    /// </summary>
    private sealed class SnapshotDb : IDisposable
    {
        private readonly SqliteConnection _connection = new("Filename=:memory:");

        public SnapshotDb(bool broken = false)
        {
            _connection.Open();
            if (broken) return;
            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public void Register(IServiceCollection services)
        {
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        }

        public async Task AddRunAsync(Guid runId)
        {
            await using var db = CreateContext();
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{runId:N}", RecoveryPolicy = RecoveryPolicy.AutoResume };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            db.LoopTemplates.Add(template);
            db.LoopTemplateVersions.Add(version);
            db.LoopRuns.Add(new LoopRun
            {
                Id = runId,
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                Status = LoopRunStatus.Running,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SeedAsync(Guid runId, string sessionId, string content, string format = "codex-jsonl")
        {
            await AddRunAsync(runId);
            await using var db = CreateContext();
            await SessionSnapshotTestSupport.SeedAsync(
                new AdapterSessionSnapshotStore(db), Key(runId, sessionId), format, content, TestContext.Current.CancellationToken);
        }

        public async Task<string?> GetAsync(Guid runId, string sessionId)
        {
            await using var db = CreateContext();
            return await SessionSnapshotTestSupport.ReadAllTextAsync(
                new AdapterSessionSnapshotStore(db), Key(runId, sessionId), TestContext.Current.CancellationToken);
        }

        public async Task<SessionSnapshotMetadata?> MetadataAsync(Guid runId, string sessionId)
        {
            await using var db = CreateContext();
            return await new AdapterSessionSnapshotStore(db).GetMetadataAsync(Key(runId, sessionId), TestContext.Current.CancellationToken);
        }

        public void Dispose() => _connection.Dispose();

        private static SessionSnapshotKey Key(Guid runId, string sessionId) => SessionSnapshotKey.ForRun(runId, "Codex", sessionId);

        private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    }
}
