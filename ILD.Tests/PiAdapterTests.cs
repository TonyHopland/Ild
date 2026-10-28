using System.Runtime.Versioning;
using System.Text;
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

[SupportedOSPlatform("linux")]
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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        const string stored = "{\"type\":\"session\",\"id\":\"pi-session-restore\"}\n{\"type\":\"message\",\"text\":\"héllo\"}";
        await harness.SeedSnapshotAsync(runId, "Pi", "pi-session-restore", stored);

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
            Assert.Equal(Encoding.UTF8.GetBytes(stored), await File.ReadAllBytesAsync(restoredSessionPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task A_turn_that_extends_the_session_file_stores_only_the_new_lines_and_restores_byte_identical()
    {
        await using var harness = await CreateSessionHarnessAsync();
        var runId = Guid.NewGuid();
        await harness.SeedRunAsync(runId);
        const string sessionId = "pi-session-append";
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-append-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = WriteTurnPi(worktreeDir, sessionId);
        var sessionPath = Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", runId.ToString("N"), $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var firstTurn = Encoding.UTF8.GetBytes($"{{\"type\":\"session\",\"version\":3,\"id\":\"{sessionId}\",\"cwd\":\"/w\"}}\n{{\"type\":\"message\",\"text\":\"one\"}}\n");
        var secondTurn = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"text\":\"two ✓\"}\n");

        try
        {
            var adapter = new PiAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            AgentExecutionContext Turn(int count) => BuildContext(
                binaryPath: scriptPath, prompt: "prompt", worktreePath: worktreeDir, runId: runId,
                sessionId: sessionId, executionCount: count, manageSession: true);

            await File.WriteAllBytesAsync(sessionPath, firstTurn, TestContext.Current.CancellationToken);
            Assert.True((await adapter.ExecuteAsync(Turn(1))).Success);
            var afterFirst = await harness.GetMetadataAsync(runId, "Pi", sessionId);

            await using (var file = new FileStream(sessionPath, FileMode.Append))
                await file.WriteAsync(secondTurn, TestContext.Current.CancellationToken);
            Assert.True((await adapter.ExecuteAsync(Turn(2))).Success);
            var afterSecond = await harness.GetMetadataAsync(runId, "Pi", sessionId);

            Assert.NotNull(afterFirst);
            Assert.NotNull(afterSecond);
            Assert.Equal("pi-jsonl", afterSecond!.Format);
            Assert.Equal(afterFirst!.ChunkCount + 1, afterSecond.ChunkCount);
            Assert.Equal(firstTurn.Length + secondTurn.Length, afterSecond.ByteLength);

            File.Delete(sessionPath);
            Assert.True((await adapter.ExecuteAsync(Turn(3))).Success);

            Assert.Equal(firstTurn.Concat(secondTurn).ToArray(), await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
            CleanUpRunScratch(runId);
        }
    }

    [Fact]
    public async Task A_stored_snapshot_of_another_format_is_never_restored_as_the_session_file()
    {
        await using var harness = await CreateSessionHarnessAsync();
        var runId = Guid.NewGuid();
        await harness.SeedRunAsync(runId);
        const string sessionId = "pi-session-foreign";
        await harness.SeedSnapshotAsync(runId, "Pi", sessionId, "{\"format\":\"claude-jsonl\",\"events\":[]}", format: "raw");
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = WriteTurnPi(worktreeDir, sessionId);
        var sessionPath = Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", runId.ToString("N"), $"{sessionId}.jsonl");

        try
        {
            var adapter = new PiAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath, prompt: "prompt", worktreePath: worktreeDir, runId: runId,
                sessionId: sessionId, executionCount: 1, manageSession: true));

            Assert.False(File.Exists(sessionPath));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
            CleanUpRunScratch(runId);
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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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

            // Source session is byte-for-byte unchanged after the fork.
            Assert.Equal(Encoding.UTF8.GetBytes(sourceJson), await harness.ReadSnapshotAsync(runId, "Pi", "source-sess"));
            // A copy now exists under the fork's id, retargeted to that id.
            var fork = Encoding.UTF8.GetString(Assert.IsType<byte[]>(await harness.ReadSnapshotAsync(runId, "Pi", "fork-dest")));
            Assert.Contains("fork-dest", fork);
            Assert.DoesNotContain("source-sess", fork);
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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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

    private static void MakeExecutable(string scriptPath)
    {
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    /// <summary>
    /// A stand-in pi that completes a turn on <paramref name="sessionId"/> and leaves
    /// the session file as the test wrote it.
    /// </summary>
    private static string WriteTurnPi(string worktreeDir, string sessionId)
    {
        var scriptPath = Path.Combine(worktreeDir, "turn.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            $"echo '{{\"type\":\"session\",\"version\":3,\"id\":\"{sessionId}\",\"cwd\":\"/w\"}}'\n" +
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

        public async Task SeedSnapshotAsync(Guid runId, string adapterName, string sessionId, string content, string format = "pi-jsonl")
        {
            await using var db = CreateDbContext();
            await SessionSnapshotTestSupport.SeedAsync(
                new AdapterSessionSnapshotStore(db), SessionSnapshotKey.ForRun(runId, adapterName, sessionId), format, content, TestContext.Current.CancellationToken);
        }

        public async Task<byte[]?> ReadSnapshotAsync(Guid runId, string adapterName, string sessionId)
        {
            await using var db = CreateDbContext();
            return await SessionSnapshotTestSupport.ReadAllAsync(
                new AdapterSessionSnapshotStore(db), SessionSnapshotKey.ForRun(runId, adapterName, sessionId), TestContext.Current.CancellationToken);
        }

        public async Task<SessionSnapshotMetadata?> GetMetadataAsync(Guid runId, string adapterName, string sessionId)
        {
            await using var db = CreateDbContext();
            return await new AdapterSessionSnapshotStore(db).GetMetadataAsync(
                SessionSnapshotKey.ForRun(runId, adapterName, sessionId), TestContext.Current.CancellationToken);
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