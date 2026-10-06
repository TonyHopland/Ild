using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

public class ClaudeCodeAdapterTests
{
    [Fact]
    public void Metadata_advertises_claude_code_provider_type()
    {
        var adapter = new ClaudeCodeAdapter();

        Assert.Equal("ClaudeCode", adapter.Name);
        Assert.Contains("claude-code", adapter.SupportedProviderTypes);
        Assert.Contains(adapter.ConfigSchema, f => f.Name == "customMcpServersJson");
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_binary_exits_zero()
    {
        var worktreeDir = CreateWorktree();
        try
        {
            var adapter = new ClaudeCodeAdapter();
            var ctx = BuildContext(binaryPath: "/bin/true", worktreePath: worktreeDir);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_binary_not_found()
    {
        var worktreeDir = CreateWorktree();
        try
        {
            var adapter = new ClaudeCodeAdapter();
            var ctx = BuildContext(binaryPath: "/nonexistent/claude", worktreePath: worktreeDir);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("claude-code-error", result.Error);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_without_worktree()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = BuildContext(binaryPath: "/bin/true", worktreePath: "/this/does/not/exist");

        var result = await adapter.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("valid worktree path", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_reads_binaryPath_from_config()
    {
        var worktreeDir = CreateWorktree();
        try
        {
            var adapter = new ClaudeCodeAdapter();
            var ctx = BuildContext(
                binaryPath: "/nonexistent/path",
                worktreePath: worktreeDir,
                config: "{\"binaryPath\":\"/bin/true\"}");

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_extracts_assistant_text_and_session_id_from_stream_json()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-abc\"}'\n" +
            "echo '{\"type\":\"assistant\",\"session_id\":\"sess-abc\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello, world.\"}]}}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-abc\",\"is_error\":false,\"result\":\"Hello, world.\"}'\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var progress = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                progressCallback: line =>
                {
                    progress.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Equal("Hello, world.", result.Output);
            Assert.Equal("sess-abc", result.SessionId);
            Assert.Contains("Hello, world.", progress);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_streams_tool_calls_to_progress_without_polluting_output()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-t\"}'\n" +
            // printf %s so the embedded \n stays an escape inside the JSON string
            // rather than being expanded by the shell into a second line.
            "printf '%s\\n' '{\"type\":\"assistant\",\"session_id\":\"sess-t\",\"message\":{\"content\":[" +
            "{\"type\":\"text\",\"text\":\"Listing files.\"}," +
            "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"description\":\"list\",\"command\":\"ls\\n  -la\"}}]}}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-t\",\"is_error\":false,\"result\":\"Listing files.\"}'\n");
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var progress = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                progressCallback: line =>
                {
                    progress.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            // The tool call surfaces on the live stream, saying what it is doing —
            // on one line, whatever the argument's own formatting was...
            var marker = Assert.Single(progress, p => p.Contains("[tool: Bash]"));
            Assert.Equal("\n[tool: Bash] ls -la\n", marker);
            Assert.Contains("Listing files.", progress);
            // ...but never bleeds into the node's text output.
            Assert.Equal("Listing files.", result.Output);
            Assert.DoesNotContain("[tool: Bash]", result.Output);
            Assert.DoesNotContain("ls -la", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_error_when_result_is_marked_as_error()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-x\"}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-x\",\"is_error\":true,\"result\":\"upstream broke\"}'\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var ctx = BuildContext(binaryPath: scriptPath, worktreePath: worktreeDir);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("upstream broke", result.Error);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_classifies_a_usage_limit_result_error_as_an_interruption()
    {
        // The result event's own error text is the provider's message, isolated,
        // so this adapter classifies from it (parity with OpenCodeAdapter — a
        // capability one adapter has and another lacks is a parity bug, ADR-0009).
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-x\"}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-x\",\"is_error\":true,\"result\":\"Claude usage limit reached. Your limit will reset at 9:40am (UTC).\"}'\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new ClaudeCodeAdapter().ExecuteAsync(
                BuildContext(binaryPath: scriptPath, worktreePath: worktreeDir));

            Assert.False(result.Success);
            Assert.Equal(ILD.Core.Services.Interfaces.FailureKind.Interrupted, result.Failure);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_passes_resume_flag_when_session_id_is_set()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                sessionId: "resume-me-123");

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Contains("--resume", result.Output);
            Assert.Contains("resume-me-123", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_launches_with_the_providers_model()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = WriteArgvEcho(worktreeDir);

        try
        {
            var result = await new ClaudeCodeAdapter().ExecuteAsync(
                BuildContext(binaryPath: scriptPath, worktreePath: worktreeDir, model: "opus"));

            Assert.True(result.Success);
            var args = result.Output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("opus", args[Array.IndexOf(args, "--model") + 1]);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_launches_without_a_model_flag_when_the_provider_has_none()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = WriteArgvEcho(worktreeDir);

        try
        {
            var result = await new ClaudeCodeAdapter().ExecuteAsync(
                BuildContext(binaryPath: scriptPath, worktreePath: worktreeDir, model: ""));

            Assert.True(result.Success);
            Assert.DoesNotContain("--model", result.Output!.Split('\n'));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_under_NoTools_launches_with_no_tools_and_no_mcp_servers_and_otherwise_as_before(bool noTools)
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = WriteArgvEcho(worktreeDir);
        var config = JsonSerializer.Serialize(new
        {
            binaryPath = scriptPath,
            customMcpServersJson = """{ "docs": { "command": ["docs-mcp"] } }""",
        });

        try
        {
            var result = await new ClaudeCodeAdapter().ExecuteAsync(
                BuildContext(binaryPath: scriptPath, worktreePath: worktreeDir, config: config) with
                {
                    ToolAllowlist = ["read", "ild"],
                    AdditionalAllowedDirectories = ["/data/worktrees/wi-99"],
                    NoTools = noTools,
                });

            Assert.True(result.Success, result.Error);
            // Empty lines kept: `--tools ""` is an empty argument.
            var args = result.Output!.Split('\n');
            if (noTools)
            {
                Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]);
                Assert.Contains("--strict-mcp-config", args);
                Assert.Contains("--no-session-persistence", args);
                Assert.DoesNotContain("--mcp-config", args);
                Assert.DoesNotContain("--add-dir", args);
                Assert.DoesNotContain("--permission-mode", args);
            }
            else
            {
                Assert.DoesNotContain("--tools", args);
                Assert.DoesNotContain("--strict-mcp-config", args);
                Assert.DoesNotContain("--no-session-persistence", args);
                Assert.Contains("--mcp-config", args);
                Assert.Equal(2, args.Count(a => a == "--add-dir"));
                Assert.Equal("bypassPermissions", args[Array.IndexOf(args, "--permission-mode") + 1]);
            }
            Assert.Equal("test prompt", args[Array.IndexOf(args, "--") + 1]);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_launches_with_the_extra_arguments_after_ilds_flags_and_before_the_prompt()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = WriteArgvEcho(worktreeDir);
        string ConfigWith(string extraArgs) => JsonSerializer.Serialize(new { binaryPath = scriptPath, extraArgs });

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                sessionId: "s-1",
                model: "opus",
                config: ConfigWith("--effort high\n--append-system-prompt \"be brief; $(touch pwned) | y\"")));

            Assert.True(result.Success, result.Error);
            var args = result.Output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(
                ["--effort", "high", "--append-system-prompt", "be brief; $(touch pwned) | y", "--", "test prompt"],
                args[^6..]);
            var firstExtra = args.Length - 6;
            foreach (var ildFlag in new[] { "--print", "--output-format", "--verbose", "--permission-mode", "--model", "--resume" })
                Assert.InRange(Array.IndexOf(args, ildFlag), 0, firstExtra - 1);
            Assert.False(File.Exists(Path.Combine(worktreeDir, "pwned")));

            // An edited value applies to the next launch of the same adapter.
            var next = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath, worktreePath: worktreeDir, config: ConfigWith("--effort low")));

            var nextArgs = next.Output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["--effort", "low", "--", "test prompt"], nextArgs[^4..]);
            Assert.DoesNotContain("high", nextArgs);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_launches_without_extra_arguments_it_cannot_split_and_logs_a_warning_naming_the_provider()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = WriteArgvEcho(worktreeDir);
        var logger = new RecordingLogger();

        try
        {
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                config: JsonSerializer.Serialize(new { binaryPath = scriptPath, extraArgs = "--effort \"high" }));
            ctx.Provider.Id = Guid.NewGuid();

            var result = await new ClaudeCodeAdapter(logger).ExecuteAsync(ctx);

            Assert.True(result.Success, result.Error);
            var args = result.Output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain("--effort", args);
            Assert.Equal(["--", "test prompt"], args[^2..]);
            Assert.Contains(logger.Warnings, w => w.Contains(ctx.Provider.Id.ToString()) || w.Contains(ctx.Provider.Name));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    private sealed class RecordingLogger : ILogger<ClaudeCodeAdapter>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>A stand-in for the claude binary that prints the argv it was launched with, one token per line.</summary>
    private static string WriteArgvEcho(string worktreeDir)
    {
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();
        return scriptPath;
    }

    [Fact]
    public void BuildRunProcessStartInfo_emits_expected_arguments()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: "abc");

        Assert.Equal("/tmp/wt", psi.WorkingDirectory);
        Assert.Equal(new[]
        {
            "--print",
            "--output-format",
            "stream-json",
            "--verbose",
            "--add-dir",
            "/tmp/wt",
            "--permission-mode",
            "bypassPermissions",
            "--resume",
            "abc",
            "--",
            "fix it",
        }, psi.ArgumentList);
    }

    [Fact]
    public void BuildRunProcessStartInfo_passes_the_model_verbatim()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null,
            model: "claude-opus-5");

        var args = psi.ArgumentList.ToList();
        Assert.Equal("claude-opus-5", args[args.IndexOf("--model") + 1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildRunProcessStartInfo_omits_model_when_blank(string? model)
    {
        // Blank means "let the CLI pick its own default": the flag has to be
        // absent entirely, never present with an empty value.
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null,
            model: model);

        Assert.DoesNotContain("--model", psi.ArgumentList);
    }

    [Fact]
    public void ClaudeCode_declares_model_support_as_optional()
    {
        Assert.Equal(AdapterModelSupport.Optional, new ClaudeCodeAdapter().ModelSupport);
    }

    [Fact]
    public void BuildRunProcessStartInfo_omits_resume_when_session_id_is_null()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null);

        Assert.DoesNotContain("--resume", psi.ArgumentList);
    }

    [Fact]
    public void BuildRunProcessStartInfo_grants_extra_allowed_directories_as_add_dir()
    {
        // ADR-0011: the Chat Context's open work item active-run worktree is
        // granted via an additional --add-dir without changing the cwd. The
        // worktree itself is already added once and must not be duplicated.
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null,
            mcpConfigPath: null,
            additionalAllowedDirectories: new[] { "/tmp/wt", "/data/worktrees/wi-99" });

        var args = psi.ArgumentList.ToList();
        // Two --add-dir occurrences: the cwd worktree + the one extra grant.
        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Contains("/data/worktrees/wi-99", args);
        // The extra dir is added after the cwd worktree's --add-dir pair.
        var extraIndex = args.IndexOf("/data/worktrees/wi-99");
        Assert.Equal("--add-dir", args[extraIndex - 1]);
    }

    [Fact]
    public void EncodeWorktreePath_replaces_slashes_with_dashes()
    {
        Assert.Equal("-workspaces-Ild", ClaudeCodeAdapter.EncodeWorktreePath("/workspaces/Ild"));
    }

    [Fact]
    public void EncodeWorktreePath_replaces_dots_with_dashes()
    {
        // Claude maps '.' to '-' as well as '/'. A dotted worktree path (the run
        // worktree slug contains them) must encode the same way Claude does, or
        // session snapshot persist/restore silently targets the wrong directory.
        Assert.Equal(
            "-home-ild-wi-22-run-a1-b2",
            ClaudeCodeAdapter.EncodeWorktreePath("/home/ild/wi-22-run-a1.b2"));
    }

    [Fact]
    public async Task ExecuteAsync_invokes_OnSessionId_once_with_first_session_id()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-live\"}'\n" +
            "echo '{\"type\":\"assistant\",\"session_id\":\"sess-live\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-live\",\"is_error\":false,\"result\":\"hi\"}'\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                onSessionId: sid => captured.Add(sid));

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            // The session id surfaces on every event but the callback fires once.
            Assert.Single(captured);
            Assert.Equal("sess-live", captured.Single());
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public void WrapJsonl_roundtrips_through_UnwrapJsonl()
    {
        var jsonl =
            "{\"type\":\"user\",\"text\":\"hello\"}\n" +
            "{\"type\":\"assistant\",\"text\":\"hi\"}\n";

        var wrapped = ClaudeCodeAdapter.WrapJsonl("sess-1", jsonl);

        // Wrapper is valid JSON the UI can parse for `messages`/`events` counts.
        using (var doc = System.Text.Json.JsonDocument.Parse(wrapped))
        {
            Assert.Equal("claude-jsonl", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("sess-1", doc.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("events").GetArrayLength());
        }

        var roundtrip = ClaudeCodeAdapter.UnwrapJsonl(wrapped);
        Assert.NotNull(roundtrip);

        var lines = roundtrip!
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"hello\"", lines[0]);
        Assert.Contains("\"hi\"", lines[1]);
    }

    [Fact]
    public void WrapJsonl_skips_malformed_lines()
    {
        var jsonl =
            "{\"type\":\"ok\"}\n" +
            "not-json\n" +
            "{\"type\":\"also-ok\"}\n";

        var wrapped = ClaudeCodeAdapter.WrapJsonl("sess", jsonl);

        using var doc = System.Text.Json.JsonDocument.Parse(wrapped);
        Assert.Equal(2, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void UnwrapJsonl_returns_null_for_unrelated_json()
    {
        Assert.Null(ClaudeCodeAdapter.UnwrapJsonl("\"just-a-string\""));
        Assert.Null(ClaudeCodeAdapter.UnwrapJsonl("{\"unrelated\":true}"));
        Assert.Null(ClaudeCodeAdapter.UnwrapJsonl(""));
        Assert.Null(ClaudeCodeAdapter.UnwrapJsonl("not json at all"));
    }

    private static string CreateWorktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ild-claude-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakeExecutable(string scriptPath)
    {
        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(scriptPath);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start chmod");
        proc.WaitForExit();
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string worktreePath,
        string prompt = "test prompt",
        string? config = null,
        string? sessionId = null,
        Func<string, Task>? progressCallback = null,
        Action<string>? onSessionId = null,
        string model = "")
    {
        var mergedConfig = config;
        if (string.IsNullOrEmpty(mergedConfig))
            mergedConfig = $"{{\"binaryPath\":\"{binaryPath}\"}}";

        return new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "claude-test",
                Type = "claude-code",
                BaseUrl = string.Empty,
                ApiKey = null,
                Model = model,
                Config = mergedConfig,
            },
            Prompt: prompt,
            RunContext: new LoopRunContext(
                Guid.NewGuid(),
                Guid.NewGuid().ToString(),
                "Test Task",
                "Test description",
                worktreePath,
                "main",
                new List<string>(),
                null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            ProgressCallback: progressCallback,
            SessionId: sessionId,
            OnSessionId: onSessionId);
    }
}
