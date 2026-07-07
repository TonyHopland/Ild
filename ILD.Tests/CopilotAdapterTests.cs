using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

public class CopilotAdapterTests
{
    [Fact]
    public void Metadata_advertises_copilot_provider_type()
    {
        var adapter = new CopilotAdapter();

        Assert.Equal("Copilot", adapter.Name);
        Assert.Contains("copilot", adapter.SupportedProviderTypes);
        Assert.Empty(adapter.ConfigSchema);
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_binary_exits_zero()
    {
        var worktreeDir = CreateWorktree();
        try
        {
            var adapter = new CopilotAdapter();
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
            var adapter = new CopilotAdapter();
            var ctx = BuildContext(binaryPath: "/nonexistent/copilot", worktreePath: worktreeDir);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("copilot-error", result.Error);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_without_worktree()
    {
        var adapter = new CopilotAdapter();
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
            var adapter = new CopilotAdapter();
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
    public async Task ExecuteAsync_returns_stdout_as_output_and_streams_progress()
    {
        var worktreeDir = CreateWorktree();
        var scriptPath = Path.Combine(worktreeDir, "fake-copilot.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo 'Working on it.'\n" +
            "echo 'All done.'\n");
        Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new CopilotAdapter();
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
            Assert.Equal("Working on it.\nAll done.", result.Output);
            // Each stdout line is relayed to the live stream as it arrives.
            Assert.Contains("Working on it.", progress);
            Assert.Contains("All done.", progress);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_binary_exits_nonzero()
    {
        var worktreeDir = CreateWorktree();
        try
        {
            var adapter = new CopilotAdapter();
            var ctx = BuildContext(binaryPath: "/bin/false", worktreePath: worktreeDir);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("exit=1", result.Error);
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
            var adapter = new CopilotAdapter();
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
        var psi = CopilotAdapter.BuildRunProcessStartInfo(
            binaryPath: "copilot",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
            sessionId: "abc");

        Assert.Equal("/tmp/wt", psi.WorkingDirectory);
        Assert.Equal(new[]
        {
            "--allow-all-tools",
            "--add-dir",
            "/tmp/wt",
            "--resume",
            "abc",
            "--prompt",
            "fix it",
        }, psi.ArgumentList);
    }

    [Fact]
    public void BuildRunProcessStartInfo_omits_resume_when_session_id_is_null()
    {
        var psi = CopilotAdapter.BuildRunProcessStartInfo(
            binaryPath: "copilot",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
            sessionId: null);

        Assert.DoesNotContain("--resume", psi.ArgumentList);
    }

    [Fact]
    public void BuildRunProcessStartInfo_grants_extra_allowed_directories_as_add_dir()
    {
        // ADR-0011: the Chat Context's open work item active-run worktree is
        // granted via an additional --add-dir. The worktree itself is already
        // added once and must not be duplicated.
        var psi = CopilotAdapter.BuildRunProcessStartInfo(
            binaryPath: "copilot",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
            sessionId: null,
            additionalAllowedDirectories: new[] { "/tmp/wt", "/data/worktrees/wi-99" });

        var args = psi.ArgumentList.ToList();
        // Two --add-dir occurrences: the cwd worktree + the one extra grant.
        Assert.Equal(2, args.Count(a => a == "--add-dir"));
        Assert.Contains("/data/worktrees/wi-99", args);
        var extraIndex = args.IndexOf("/data/worktrees/wi-99");
        Assert.Equal("--add-dir", args[extraIndex - 1]);
    }

    private static string CreateWorktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ild-copilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string worktreePath,
        string prompt = "test prompt",
        string? config = null,
        string? sessionId = null,
        Func<string, Task>? progressCallback = null)
    {
        var mergedConfig = config;
        if (string.IsNullOrEmpty(mergedConfig))
            mergedConfig = $"{{\"binaryPath\":\"{binaryPath}\"}}";

        return new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "copilot-test",
                Type = "copilot",
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
            SessionId: sessionId);
    }
}
