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

[Collection("EnvironmentPath")]
public class PiAdapterTests
{
    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new PiAdapter();

        Assert.Equal("Pi", adapter.Name);
        Assert.Contains("pi", adapter.SupportedProviderTypes);
        Assert.Empty(adapter.ConfigSchema);
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
        var scriptPath = Path.Combine(worktreeDir, "pi");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-http-base\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", worktreeDir + Path.PathSeparator + previousPath);

        try
        {
            var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
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
            Environment.SetEnvironmentVariable("PATH", previousPath);
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
            Assert.Contains("read,grep,find,ls,edit,write,bash,ild_", result.Output);
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
            Assert.Contains("pi-session-restore", (await File.ReadAllTextAsync(restoredSessionPath)));
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
            var source = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "Pi" && s.SessionId == "source-sess");
            Assert.NotNull(source);
            Assert.Equal(sourceJson, source!.SessionJson);
            // A copy now exists under the fork's id, retargeted to that id.
            var fork = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "Pi" && s.SessionId == "fork-dest");
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
            "{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-header-match\",\"cwd\":\"/tmp/worktree\"}\n");

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
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(extension)!, "ild-mcp-bridge.js")));
            UnixOwnership.AssertOrchestratorOwned(Path.GetDirectoryName(extension)!, UnixOwnership.AgentReadDirectory);
            UnixOwnership.AssertOrchestratorOwned(extension, UnixOwnership.AgentReadFile);
            UnixOwnership.AssertOrchestratorOwned(
                Path.Combine(Path.GetDirectoryName(extension)!, "ild-mcp-bridge.js"), UnixOwnership.AgentReadFile);

            var ildTs = File.ReadAllText(extension);
            Assert.Contains("./ild-mcp-bridge.js", ildTs);
            Assert.Contains("ild-mcp-server.dll", ildTs);
            Assert.Contains("ILD_LOOP_RUN_ID", ildTs);
            Assert.Contains(runId.ToString(), ildTs);
            Assert.DoesNotContain("ILD_CHAT_SESSION_ID", ildTs);

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
                .Concat(McpServerToolReflection.Names().Select(n => "ild_" + n));
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
            var modelsJson = await File.ReadAllTextAsync(modelsJsonPath);

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
        Action<string>? onSessionId = null)
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
            OnSessionId: onSessionId);
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