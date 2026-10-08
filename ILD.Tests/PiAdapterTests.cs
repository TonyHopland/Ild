using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class PiAdapterTests
{
    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new PiAdapter();

        Assert.Equal("Pi", adapter.Name);
        Assert.Contains("pi", adapter.SupportedProviderTypes);
        // A BYO-endpoint provider's model is part of its connection details, so
        // blanking it stays a validation error.
        Assert.Equal(AdapterModelSupport.Required, adapter.ModelSupport);
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_binary_not_found()
    {
        var adapter = new PiAdapter();

        var result = await adapter.ExecuteAsync(BuildContext(
            binaryPath: "/nonexistent/pi",
            executionCount: 1));

        Assert.False(result.Success);
        Assert.Contains("pi-error", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_reads_text_and_session_id_from_json_events()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-123\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_update\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"hello \"}}'\n" +
            "echo '{\"type\":\"message_update\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"world\"}}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"hello world\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new PiAdapter();
            var progress = new System.Collections.Concurrent.ConcurrentBag<string>();

            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                progressCallback: chunk =>
                {
                    progress.Add(chunk);
                    return Task.CompletedTask;
                }));

            Assert.True(result.Success);
            Assert.Equal("hello world", result.Output);
            Assert.Equal("pi-session-123", result.SessionId);
            Assert.Contains("hello ", progress);
            Assert.Contains("world", progress);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_streams_tool_calls_to_progress_without_polluting_output()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-tool\",\"cwd\":\"$PWD\"}'\n" +
            "printf '%s\\n' '{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"bash\",\"args\":{\"command\":\"npm\\n  test\"}}'\n" +
            "echo '{\"type\":\"message_update\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"ran the tests\"}}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ran the tests\"}]}}'\n");
        MakeExecutable(scriptPath);

        try
        {
            var progress = new System.Collections.Concurrent.ConcurrentBag<string>();

            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                progressCallback: chunk =>
                {
                    progress.Add(chunk);
                    return Task.CompletedTask;
                }));

            Assert.True(result.Success);
            Assert.Contains("\n[tool: bash] npm test\n", progress);
            Assert.Equal("ran the tests", result.Output);
            Assert.DoesNotContain("[tool:", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_invokes_OnSessionId_once_on_session_event()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-sid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-live\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_update\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"hi\"}}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"hi\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                onSessionId: sid => captured.Add(sid)));

            Assert.True(result.Success);
            Assert.Single(captured);
            Assert.Equal("pi-live", captured.Single());
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_stream_ends_before_turn_end()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-truncated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-trunc\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_update\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"partial response\"}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1));

            Assert.False(result.Success);
            Assert.Contains("truncated", result.Error);
            Assert.Equal("partial response", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ignores_http_base_url_when_binary_path_is_not_configured()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-baseurl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        // No binaryPath, so the adapter falls back to the managed install under ILD_DATA_PATH.
        var dataRoot = Path.Combine(worktreeDir, "data");
        var scriptPath = ManagedAgentInstall.BinaryIn(
            ManagedAgentInstall.VersionDir(dataRoot, ManagedAgentCatalog.Pi, "v1"), ManagedAgentCatalog.Pi);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(ManagedAgentInstall.PointerFile(dataRoot, ManagedAgentCatalog.Pi), "v1");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-http-base\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new PiAdapter(new TestProcessEnvironment { { "ILD_DATA_PATH", dataRoot } }).ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "pi",
                    BaseUrl = "http://localhost:1234/v1",
                    Model = "openai/gpt-5",
                    Config = null,
                },
                Prompt: "test prompt",
                RunContext: new LoopRunContext(
                    Guid.NewGuid(),
                    Guid.NewGuid().ToString(),
                    "Test Task",
                    "Test description",
                    worktreeDir,
                    "main",
                    new List<string>(),
                    null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None));

            Assert.True(result.Success);
            Assert.Equal("ok", result.Output);
            Assert.Null(result.Error);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_passes_provider_model_and_session_path_when_stdout_is_not_json()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\"\n" +
            "echo STDIN-BEGIN\n" +
            "cat\n" +
            "echo STDIN-END\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new PiAdapter();
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "my prompt",
                worktreePath: worktreeDir,
                model: "openai/gpt-5",
                apiKey: "sk-test",
                sessionId: "pi-session-existing",
                executionCount: 1,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Contains("--mode", result.Output);
            Assert.Contains("json", result.Output);
            Assert.Contains("--session-dir", result.Output);
            Assert.Contains("--session", result.Output);
            Assert.Contains("--tools", result.Output);
            Assert.Contains("read,grep,find,ls,edit,write,bash,mcp__ild__", result.Output);
            Assert.Contains("openai/gpt-5", result.Output);
            Assert.Contains("sk-test", result.Output);
            Assert.DoesNotContain("\n--\n", result.Output);
            Assert.Contains("STDIN-BEGIN", result.Output);
            Assert.Contains("my prompt", result.Output);
            Assert.Contains("STDIN-END", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_restores_snapshot_when_managed_session_file_is_missing()
    {
        await using var harness = await CreateSessionHarnessAsync();
        var runId = Guid.NewGuid();
        await harness.SeedRunAsync(runId);

        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "printf '%s\\n' \"$@\"\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-restore\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        await harness.SeedSnapshotAsync(runId, "Pi", "pi-session-restore", "{\"type\":\"session\",\"id\":\"pi-session-restore\"}\n");

        try
        {
            var adapter = new PiAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "prompt",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "pi-session-restore",
                executionCount: 1,
                manageSession: true));

            Assert.True(result.Success);
            var restoredSessionPath = Path.Combine(Path.GetTempPath(), "ild-pi-sessions", runId.ToString("N"), "pi-session-restore.jsonl");
            Assert.True(File.Exists(restoredSessionPath));
            Assert.Contains("pi-session-restore", (await File.ReadAllTextAsync(restoredSessionPath, TestContext.Current.CancellationToken)));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_forks_source_snapshot_under_new_id_leaving_source_unchanged()
    {
        await using var harness = await CreateSessionHarnessAsync();
        var runId = Guid.NewGuid();
        await harness.SeedRunAsync(runId);

        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "fork.sh");
        // Minimal pi stub: drain the prompt on stdin and exit, so the only
        // snapshot writes come from the fork copy + the post-run persist.
        File.WriteAllText(scriptPath, "#!/bin/sh\ncat >/dev/null\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        const string sourceJson = "{\"type\":\"session\",\"version\":3,\"id\":\"source-sess\",\"cwd\":\"/w\"}\n";
        await harness.SeedSnapshotAsync(runId, "Pi", "source-sess", sourceJson);

        try
        {
            var adapter = new PiAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "prompt",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "fork-dest",
                executionCount: 1,
                manageSession: true) with { ForkFromSessionId = "source-sess" });

            await using var verifyDb = harness.CreateDbContext();
            // Source session is byte-for-byte unchanged after the fork.
            var source = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "Pi" && s.SessionId == "source-sess", TestContext.Current.CancellationToken);
            Assert.NotNull(source);
            Assert.Equal(sourceJson, source!.SessionJson);
            // A copy now exists under the fork's id, retargeted to that id.
            var fork = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "Pi" && s.SessionId == "fork-dest", TestContext.Current.CancellationToken);
            Assert.NotNull(fork);
            Assert.Contains("fork-dest", fork!.SessionJson);
            Assert.DoesNotContain("source-sess", fork.SessionJson);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_restores_local_session_file_by_header_id_when_filename_differs()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-local-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);

        var sessionDir = Path.Combine(Path.GetTempPath(), "ild-pi-sessions", runId.ToString("N"), "--tmp-worktree--");
        Directory.CreateDirectory(sessionDir);
        var actualSessionPath = Path.Combine(sessionDir, $"{DateTime.UtcNow:yyyyMMddHHmmss}_abcdef12.jsonl");
        await File.WriteAllTextAsync(
            actualSessionPath,
            "{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-header-match\",\"cwd\":\"/tmp/worktree\"}\n", TestContext.Current.CancellationToken);

        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\"\n" +
            "cat >/dev/null\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new PiAdapter();
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "prompt",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "pi-session-header-match",
                executionCount: 1,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Contains("--session", result.Output);
            Assert.Contains(actualSessionPath, result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_sends_prompt_starting_with_dashes_via_stdin()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-dashes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "stdin.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\"\n" +
            "echo STDIN-BEGIN\n" +
            "cat\n" +
            "echo STDIN-END\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new PiAdapter();
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "---\nname: to-issues\n",
                worktreePath: worktreeDir,
                executionCount: 1));

            Assert.True(result.Success);
            Assert.DoesNotContain("Unknown option", result.Output);
            Assert.Contains("STDIN-BEGIN", result.Output);
            Assert.Contains("---", result.Output);
            Assert.Contains("name: to-issues", result.Output);
            Assert.Contains("STDIN-END", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }


    [Fact]
    public async Task ExecuteAsync_loads_the_ild_mcp_extension_for_a_run_even_without_an_absolute_base_url()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-run");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            var extension = ExtensionArgument(worktreeDir);
            Assert.Equal(Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", runId.ToString("N"), "ild.ts"), extension);
            Assert.Equal(new[] { extension }, Directory.GetFileSystemEntries(Path.GetDirectoryName(extension)!));
            UnixOwnership.AssertOrchestratorOwned(Path.GetDirectoryName(extension)!, UnixOwnership.AgentReadDirectory);
            UnixOwnership.AssertOrchestratorOwned(extension, UnixOwnership.AgentReadFile);

            // Pi 1.0's own MCP client starts the server; the file only registers it.
            var ildTs = File.ReadAllText(extension);
            Assert.Matches(@"registerMcpServer\(\s*""ild""\s*,", ildTs);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(ildTs, @"registerMcpServer\("));
            Assert.Matches(@"""?exposure""?\s*:\s*""direct""", ildTs);
            Assert.Matches(@"""?timeout""?\s*:\s*120\b", ildTs);
            Assert.Contains("ild-mcp-server.dll", ildTs);
            Assert.Contains("ILD_API_URL", ildTs);
            Assert.Contains("ILD_LOOP_RUN_ID", ildTs);
            Assert.Contains(runId.ToString(), ildTs);
            Assert.DoesNotContain("ILD_CHAT_SESSION_ID", ildTs);
            Assert.DoesNotContain("ild-mcp-bridge", ildTs);
            Assert.DoesNotContain("from \"./", ildTs);
            Assert.DoesNotContain("registerTool", ildTs);
            Assert.DoesNotContain("child_process", ildTs);

            // The agent dir (and its models.json) stays tied to an absolute BaseUrl.
            Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(worktreeDir, "agent-dir.txt")));
            Assert.False(Directory.Exists(Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", runId.ToString("N"))));
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_scopes_the_ild_mcp_extension_to_the_chat_session_for_a_chat_turn()
    {
        var chatSessionId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-chat");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: chatSessionId,
                executionCount: 1) with { ChatSessionId = chatSessionId });

            Assert.True(result.Success, result.Error);
            var extension = ExtensionArgument(worktreeDir);
            Assert.StartsWith(Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext") + Path.DirectorySeparatorChar, extension);
            UnixOwnership.AssertOrchestratorOwned(Path.GetDirectoryName(extension)!, UnixOwnership.AgentReadDirectory);
            UnixOwnership.AssertOrchestratorOwned(extension, UnixOwnership.AgentReadFile);

            var ildTs = File.ReadAllText(extension);
            Assert.Contains("ILD_CHAT_SESSION_ID", ildTs);
            Assert.Contains(chatSessionId.ToString(), ildTs);
            Assert.DoesNotContain("ILD_LOOP_RUN_ID", ildTs);
        }
        finally
        {
            CleanUpRunScratch(chatSessionId);
            Directory.Delete(worktreeDir, true);
        }
    }

    /// <summary>
    /// The ILD MCP server a chat turn starts is told that turn's own id, so every
    /// API call it makes names the turn; a later turn of the same chat gets its
    /// own id, never the first one's, even though the extension lives under the
    /// chat session, and a loop run gets none.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_tells_the_ild_server_each_chat_turn_its_own_id_and_a_loop_run_none()
    {
        var chatSessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        var loopRunId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-turn");
        var scriptPath = WriteExtensionCopyingPi(worktreeDir);
        var adapter = new PiAdapter();

        try
        {
            var firstTurn = await IldExtensionAsync(adapter, worktreeDir, BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: chatSessionId,
                executionCount: 1,
                chatSessionId: chatSessionId,
                chatTurnId: firstTurnId));
            Assert.Equal(firstTurnId.ToString(), ChatTurnIdIn(firstTurn));
            Assert.Contains("ILD_CHAT_SESSION_ID", firstTurn);
            Assert.Contains(chatSessionId.ToString(), firstTurn);

            var secondTurn = await IldExtensionAsync(adapter, worktreeDir, BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: chatSessionId,
                executionCount: 1,
                chatSessionId: chatSessionId,
                chatTurnId: secondTurnId));
            Assert.Equal(secondTurnId.ToString(), ChatTurnIdIn(secondTurn));
            Assert.DoesNotContain(firstTurnId.ToString(), secondTurn);

            var loopRun = await IldExtensionAsync(adapter, worktreeDir, BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: loopRunId,
                executionCount: 1));
            Assert.DoesNotContain("ILD_CHAT_TURN_ID", loopRun);
            Assert.Contains("ILD_LOOP_RUN_ID", loopRun);
            Assert.Contains(loopRunId.ToString(), loopRun);
        }
        finally
        {
            CleanUpRunScratch(chatSessionId);
            CleanUpRunScratch(loopRunId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_with_ild_off_loads_no_extension_and_lists_no_ild_tools()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-off");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                executionCount: 1) with { ToolAllowlist = new[] { "read", "write", "execute" } });

            Assert.True(result.Success, result.Error);
            var argv = File.ReadAllLines(Path.Combine(worktreeDir, "argv.txt"));
            Assert.DoesNotContain("-e", argv);
            Assert.Equal("read,grep,find,ls,edit,write,bash", argv[Array.IndexOf(argv, "--tools") + 1]);
            Assert.DoesNotContain(argv, arg => arg.Contains("mcp__", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", runId.ToString("N"))));
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_allows_exactly_the_mcp_servers_tools_alongside_the_built_ins()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-tools");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            var argv = File.ReadAllLines(Path.Combine(worktreeDir, "argv.txt"));
            var tools = argv[Array.IndexOf(argv, "--tools") + 1].Split(',');

            var expected = new[] { "read", "grep", "find", "ls", "edit", "write", "bash" }
                .Concat(McpServerToolReflection.Names().Select(n => "mcp__ild__" + n));
            Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), tools.OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_with_an_absolute_base_url_keeps_models_json_and_loads_the_extension_from_scratch()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-baseurl");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "vllm-provider",
                    Type = "pi",
                    BaseUrl = "http://localhost:8000/v1",
                    Model = "openai/my-model",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "test prompt",
                RunContext: new LoopRunContext(runId, "wi", "t", "d", worktreeDir, "main", new List<string>(), null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None));

            Assert.True(result.Success, result.Error);
            var agentDir = Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", runId.ToString("N"));
            Assert.Equal(agentDir, File.ReadAllText(Path.Combine(worktreeDir, "agent-dir.txt")));
            Assert.True(File.Exists(Path.Combine(agentDir, "models.json")));
            Assert.False(File.Exists(Path.Combine(agentDir, "extensions", "ild.ts")));

            Assert.Equal(
                Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", runId.ToString("N"), "ild.ts"),
                ExtensionArgument(worktreeDir));
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_does_not_write_ild_extension_when_no_http_base_url()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-no-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-noext\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                executionCount: 1));

            Assert.True(result.Success);

            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            Assert.False(Directory.Exists(agentDir));
        }
        finally
        {
            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            if (Directory.Exists(agentDir))
                Directory.Delete(agentDir, true);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_writes_models_json_that_interpolates_api_key_env_var()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-modelskey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "pi.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-modelskey\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "vllm-provider",
                    Type = "pi",
                    // Absolute BaseUrl takes the custom-provider path that writes models.json.
                    BaseUrl = "http://localhost:8000/v1",
                    ApiKey = "sk-secret",
                    Model = "openai/my-model",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "test prompt",
                RunContext: new LoopRunContext(
                    runId,
                    Guid.NewGuid().ToString(),
                    "Test Task",
                    "Test description",
                    worktreeDir,
                    "main",
                    new List<string>(),
                    null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None));

            Assert.True(result.Success);

            var modelsJsonPath = Path.Combine(
                Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"), "models.json");
            Assert.True(File.Exists(modelsJsonPath));
            var modelsJson = await File.ReadAllTextAsync(modelsJsonPath, TestContext.Current.CancellationToken);

            // Pi interpolates env vars only when the value is "$"-prefixed; a bare
            // uppercase name is treated as a literal key and would 401 against vLLM.
            Assert.Contains("\"apiKey\": \"$ILD_PI_PROVIDER_API_KEY\"", modelsJson);
            // The real secret must never be baked into the config file itself.
            Assert.DoesNotContain("sk-secret", modelsJson);
        }
        finally
        {
            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            if (Directory.Exists(agentDir))
                Directory.Delete(agentDir, true);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_escapes_mcp_env_values_so_pi_reads_them_back_literally()
    {
        // Pi 1.0.4 dist/core/resolve-config-value.js resolves every registered MCP
        // env value: `$NAME`/`${NAME}` expand, a leading `!` runs a shell command,
        // and `$$`/`$!` read back as `$`/`!`.
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-mcp-escape");
        var scriptPath = WriteRecordingPi(worktreeDir);
        var environment = new TestProcessEnvironment
        {
            { "ILD_API_TOKEN", "!a$b${C}" },
            { "ILD_API_URL", "http://example.com/$api" },
        };

        try
        {
            var result = await new PiAdapter(environment).ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            var ildTs = File.ReadAllText(ExtensionArgument(worktreeDir));
            Assert.Contains("$!a$$b$${C}", ildTs);
            Assert.Contains("http://example.com/$$api", ildTs);
            Assert.DoesNotContain("\"!a$b${C}\"", ildTs);
            Assert.DoesNotContain("example.com/$api", ildTs);
            Assert.Contains(runId.ToString(), ildTs);
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("gpt-5", true)]
    public async Task ExecuteAsync_passes_provider_only_together_with_a_model(string model, bool expectProvider)
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-provider");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                model: model,
                config: JsonSerializer.Serialize(new { provider = "openai" }),
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            var argv = File.ReadAllLines(Path.Combine(worktreeDir, "argv.txt"));
            if (expectProvider)
            {
                Assert.Equal("openai", argv[Array.IndexOf(argv, "--provider") + 1]);
                Assert.Equal(model, argv[Array.IndexOf(argv, "--model") + 1]);
            }
            else
            {
                Assert.DoesNotContain("--provider", argv);
                Assert.DoesNotContain("--model", argv);
            }
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Theory]
    [InlineData("--thinking high --append-system-prompt \"be brief\"", new[] { "--thinking", "high", "--append-system-prompt", "be brief" })]
    [InlineData("--thinking \"high", new string[0])]
    public async Task ExecuteAsync_launches_with_the_extra_arguments_after_all_of_ilds_flags(string extraArgs, string[] expected)
    {
        var runId = Guid.NewGuid();
        var worktreeDir = NewWorktree("ild-pi-extra-args");
        var scriptPath = WriteRecordingPi(worktreeDir);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                model: "gpt-5",
                sessionId: "pi-session-ild",
                config: JsonSerializer.Serialize(new { provider = "openai", extraArgs }),
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            var argv = File.ReadAllLines(Path.Combine(worktreeDir, "argv.txt"));
            Assert.Equal(expected, argv[^expected.Length..]);
            var firstExtra = argv.Length - expected.Length;
            foreach (var ildFlag in new[] { "--mode", "--session-dir", "--provider", "--model", "--session" })
                Assert.InRange(Array.IndexOf(argv, ildFlag), 0, firstExtra - 1);
            Assert.DoesNotContain("--thinking", argv[..firstExtra]);
        }
        finally
        {
            CleanUpRunScratch(runId);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_marks_an_ild_tool_called_from_codemode_on_the_live_stream()
    {
        // Pi 1.0.4 dist/core/nested-tool-calls.js: a tool called from codemode is
        // announced with its own tool_execution_start carrying parentToolCallId.
        var worktreeDir = NewWorktree("ild-pi-nested-tool");
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-nested\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"codemode\",\"args\":{\"code\":\"await mcp.ild.get_workitem({workItemId: 1})\"}}'\n" +
            "echo '{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1/1\",\"toolName\":\"mcp__ild__get_workitem\",\"args\":{\"workItemId\":\"wi-7\"},\"parentToolCallId\":\"call-1\"}'\n" +
            "echo '{\"type\":\"tool_execution_end\",\"toolCallId\":\"call-1/1\",\"toolName\":\"mcp__ild__get_workitem\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}]},\"isError\":false,\"parentToolCallId\":\"call-1\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}'\n");
        MakeExecutable(scriptPath);

        try
        {
            var progress = new System.Collections.Concurrent.ConcurrentBag<string>();
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                executionCount: 1,
                progressCallback: chunk =>
                {
                    progress.Add(chunk);
                    return Task.CompletedTask;
                }));

            Assert.True(result.Success, result.Error);
            Assert.Contains("\n[tool: mcp__ild__get_workitem] wi-7\n", progress);
            Assert.Equal("done", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_records_usage_summed_over_the_runs_assistant_messages()
    {
        var worktreeDir = NewWorktree("ild-pi-usage");
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        const string first = "{\"input\":100,\"output\":10,\"cacheRead\":20,\"cacheWrite\":5,\"totalTokens\":135,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.25}}";
        const string second = "{\"input\":200,\"output\":30,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":230,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.5}}";
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-usage\",\"cwd\":\"/w\"}'\n" +
            $"echo '{{\"type\":\"message_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"bash\",\"arguments\":{{}}}}],\"usage\":{first}}}}}'\n" +
            $"echo '{{\"type\":\"turn_end\",\"message\":{{\"role\":\"assistant\",\"content\":[],\"usage\":{first}}},\"toolResults\":[]}}'\n" +
            $"echo '{{\"type\":\"message_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"done\"}}],\"usage\":{second}}}}}'\n" +
            $"echo '{{\"type\":\"turn_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"done\"}}],\"usage\":{second}}},\"toolResults\":[]}}'\n");
        MakeExecutable(scriptPath);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                executionCount: 1));

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.Usage);
            Assert.Equal(325, result.Usage!.InputTokens);
            Assert.Equal(40, result.Usage.OutputTokens);
            Assert.Equal(0.75m, result.Usage.CostUsd);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    private static void MakeExecutable(string scriptPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(scriptPath);
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start chmod");
        proc.WaitForExit();
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        int executionCount,
        string? prompt = null,
        string? worktreePath = null,
        string? model = null,
        string? apiKey = null,
        string? config = null,
        Func<string, Task>? progressCallback = null,
        CancellationToken? cancel = null,
        Guid? runId = null,
        string? sessionId = null,
        bool manageSession = false,
        Action<string>? onSessionId = null,
        Guid? chatSessionId = null,
        Guid? chatTurnId = null)
    {
        var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            config ?? "{}") ?? new System.Collections.Generic.Dictionary<string, object>();
        if (!dict.ContainsKey("binaryPath"))
            dict["binaryPath"] = binaryPath;
        var mergedConfig = JsonSerializer.Serialize(dict);

        return new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test-provider",
                Type = "pi",
                BaseUrl = string.Empty,
                ApiKey = apiKey,
                Model = model ?? "openai/gpt-5",
                Config = mergedConfig
            },
            Prompt: prompt ?? "test prompt",
            RunContext: new LoopRunContext(
                runId ?? Guid.NewGuid(),
                Guid.NewGuid().ToString(),
                "Test Task",
                "Test description",
                worktreePath ?? "/tmp",
                "main",
                new List<string>(),
                null),
            ExecutionCount: executionCount,
            Cancel: cancel ?? CancellationToken.None,
            ProgressCallback: progressCallback,
            SessionId: sessionId,
            ManageSession: manageSession,
            OnSessionId: onSessionId,
            ChatSessionId: chatSessionId,
            ChatTurnId: chatTurnId);
    }

    private static string NewWorktree(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A stand-in pi that records its argv (one per line) and the agent dir it
    /// was given, then completes a turn so the adapter reports success.
    /// </summary>
    private static string WriteRecordingPi(string worktreeDir)
    {
        var scriptPath = Path.Combine(worktreeDir, "pi.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" > '{worktreeDir}/argv.txt'\n" +
            $"printf '%s' \"$PI_CODING_AGENT_DIR\" > '{worktreeDir}/agent-dir.txt'\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-ild\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        MakeExecutable(scriptPath);
        return scriptPath;
    }

    /// <summary>
    /// A stand-in pi that copies the extension it was handed with <c>-e</c> while it
    /// runs, so each launch's own extension is what gets inspected, then completes a turn.
    /// </summary>
    private static string WriteExtensionCopyingPi(string worktreeDir)
    {
        var scriptPath = Path.Combine(worktreeDir, "pi.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "prev=''\n" +
            "for a in \"$@\"; do\n" +
            $"  if [ \"$prev\" = '-e' ]; then cp \"$a\" '{worktreeDir}/ild-extension.ts'; fi\n" +
            "  prev=\"$a\"\n" +
            "done\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-turn\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        MakeExecutable(scriptPath);
        return scriptPath;
    }

    private static async Task<string> IldExtensionAsync(PiAdapter adapter, string worktreeDir, AgentExecutionContext context)
    {
        var captured = Path.Combine(worktreeDir, "ild-extension.ts");
        File.Delete(captured);

        var result = await adapter.ExecuteAsync(context);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(captured), "pi was not given -e <ild extension>");
        return File.ReadAllText(captured);
    }

    private static string ChatTurnIdIn(string ildTs)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ildTs, @"""ILD_CHAT_TURN_ID""\s*:\s*""([^""]*)""");
        Assert.True(match.Success, "the ild extension does not set ILD_CHAT_TURN_ID");
        return match.Groups[1].Value;
    }

    private static string ExtensionArgument(string worktreeDir)
    {
        var argv = File.ReadAllLines(Path.Combine(worktreeDir, "argv.txt"));
        var flag = Array.IndexOf(argv, "-e");
        Assert.True(flag >= 0, "pi was not given -e <ild extension>");
        return argv[flag + 1];
    }

    private static void CleanUpRunScratch(Guid runId)
    {
        foreach (var dir in new[]
        {
            Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", runId.ToString("N")),
            Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", runId.ToString("N")),
            Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", runId.ToString("N")),
        })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private static async Task<SessionHarness> CreateSessionHarnessAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new SessionHarness(provider, connection);
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly SqliteConnection _connection;

        public SessionHarness(ServiceProvider provider, SqliteConnection connection)
        {
            _provider = provider;
            _connection = connection;
        }

        public IServiceProvider Services => _provider;

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(options);
        }

        public async Task SeedSnapshotAsync(Guid runId, string adapterName, string sessionId, string sessionJson)
        {
            await using var db = CreateDbContext();
            db.AdapterSessionSnapshots.Add(new AdapterSessionSnapshot
            {
                LoopRunId = runId,
                AdapterName = adapterName,
                SessionId = sessionId,
                SessionJson = sessionJson,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedRunAsync(Guid runId)
        {
            await using var db = CreateDbContext();

            var templateId = Guid.NewGuid();
            var versionId = Guid.NewGuid();

            db.LoopTemplates.Add(new LoopTemplate
            {
                Id = templateId,
                Name = $"template-{runId:N}",
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });

            db.LoopTemplateVersions.Add(new LoopTemplateVersion
            {
                Id = versionId,
                LoopTemplateId = templateId,
                VersionNumber = 1,
            });

            db.LoopRuns.Add(new LoopRun
            {
                Id = runId,
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = versionId,
                Status = LoopRunStatus.Running,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });

            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}