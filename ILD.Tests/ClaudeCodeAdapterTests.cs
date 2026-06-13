using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

public class ClaudeCodeAdapterTests
{
    [Fact]
    public void Metadata_advertises_claude_code_provider_type()
    {
        var adapter = new ClaudeCodeAdapter();

        Assert.Equal("ClaudeCode", adapter.Name);
        Assert.Contains("claude-code", adapter.SupportedProviderTypes);
        Assert.Empty(adapter.ConfigSchema);
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
            "echo '{\"type\":\"assistant\",\"session_id\":\"sess-t\",\"message\":{\"content\":[" +
            "{\"type\":\"text\",\"text\":\"Listing files.\"}," +
            "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}'\n" +
            "echo '{\"type\":\"result\",\"session_id\":\"sess-t\",\"is_error\":false,\"result\":\"Listing files.\"}'\n");
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
            // The tool call surfaces on the live stream...
            Assert.Contains(progress, p => p.Contains("[tool: Bash]"));
            Assert.Contains("Listing files.", progress);
            // ...but never bleeds into the node's text output.
            Assert.Equal("Listing files.", result.Output);
            Assert.DoesNotContain("[tool: Bash]", result.Output);
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
    public void BuildRunProcessStartInfo_emits_expected_arguments()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
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
    public void BuildRunProcessStartInfo_omits_resume_when_session_id_is_null()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
            sessionId: null);

        Assert.DoesNotContain("--resume", psi.ArgumentList);
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

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string worktreePath,
        string prompt = "test prompt",
        string? config = null,
        string? sessionId = null,
        Func<string, Task>? progressCallback = null,
        Action<string>? onSessionId = null)
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
                Model = string.Empty,
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
