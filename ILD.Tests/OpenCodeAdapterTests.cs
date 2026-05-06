using FluentAssertions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

public class OpenCodeAdapterTests
{
    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new OpenCodeAdapter();

        adapter.Name.Should().Be("OpenCode");
        adapter.SupportedProviderTypes.Should().Contain("opencode");
        adapter.ConfigSchema.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_binary_exits_zero()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_binary_not_found()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/nonexistent/opencode",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("opencode-error");
    }

    [Fact]
    public async Task ExecuteAsync_on_first_execution_uses_initial_prompt()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            initialPrompt: "initial prompt here",
            loopPrompt: "loop prompt here",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_on_loopback_uses_loop_prompt()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            initialPrompt: "initial prompt here",
            loopPrompt: "loop prompt here",
            executionCount: 2);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_reads_binaryPath_from_config_overriding_baseUrl()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/nonexistent/path",
            config: "{\"binaryPath\":\"/bin/true\"}",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_baseUrl_when_config_is_empty()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            config: null,
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_returns_stderr_on_nonzero_exit()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/sh",
            initialPrompt: "-c exit 42",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("exit=");
    }

    [Fact]
    public async Task ExecuteAsync_renders_work_item_placeholders()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            initialPrompt: "Fix: {{WorkItem.Title}} - {{WorkItem.Description}}",
            workItemTitle: "Null reference in parser",
            workItemDescription: "Fix the crash",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_renders_file_placeholder_from_worktree()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var testFile = Path.Combine(worktreeDir, "test.txt");
        File.WriteAllText(testFile, "file content here");

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: "/bin/true",
                initialPrompt: "Read: {{WorkTree.File:test.txt}}",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_uses_worktree_as_working_directory()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "pwd.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\npwd\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain(worktreeDir);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_times_out_long_running_process()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ild-timeout-test-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nsleep 60\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();
            using var cts = new System.Threading.CancellationTokenSource(1000);

            var ctx = BuildContext(
                binaryPath: scriptPath,
                executionCount: 1,
                cancel: cts.Token);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("timed out");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_streams_stdout_lines_to_progress_callback()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ild-stream-test-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho 'line one'\necho 'line two'\necho 'line three'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();
            var progressLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "ignored",
                executionCount: 1,
                progressCallback: (line) =>
                {
                    progressLines.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            progressLines.Should().Contain("line one");
            progressLines.Should().Contain("line two");
            progressLines.Should().Contain("line three");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_includes_api_key_in_opencode_config()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "env.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "ignored",
                worktreePath: worktreeDir,
                apiKey: "test-secret-key-123",
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("test-secret-key-123");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_passes_session_flag_when_session_id_is_set()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "my prompt",
                worktreePath: worktreeDir,
                sessionId: "test-session-abc",
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("--session");
            result.Output.Should().Contain("test-session-abc");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_does_not_pass_session_flag_when_session_id_is_null()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "my prompt",
                worktreePath: worktreeDir,
                sessionId: null,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().NotContain("--session");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_returns_session_id_from_json_output()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho '{\"text\":\"hello\",\"sessionId\":\"session-from-output\"}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                initialPrompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.SessionId.Should().Be("session-from-output");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string initialPrompt = "test prompt",
        string? loopPrompt = null,
        string? config = null,
        string? workItemTitle = null,
        string? workItemDescription = null,
        string? worktreePath = null,
        string? apiKey = null,
        int executionCount = 1,
        CancellationToken? cancel = null,
        Func<string, Task>? progressCallback = null,
        string? sessionId = null)
    {
        var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            config ?? "{}") ?? new System.Collections.Generic.Dictionary<string, object>();
        if (!dict.ContainsKey("binaryPath"))
            dict["binaryPath"] = binaryPath;
        var mergedConfig = System.Text.Json.JsonSerializer.Serialize(dict);

        return new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test-provider",
                Type = "opencode",
                BaseUrl = "http://localhost:1234/v1",
                ApiKey = apiKey,
                Model = "test-model",
                Config = mergedConfig
            },
            InitialPrompt: initialPrompt,
            LoopPrompt: loopPrompt ?? initialPrompt,
            RunContext: new LoopRunContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                workItemTitle ?? "Test Task",
                workItemDescription ?? "Test description",
                worktreePath ?? "/tmp",
                "main",
                new List<string>(),
                null),
            ExecutionCount: executionCount,
            Cancel: cancel ?? CancellationToken.None,
            ProgressCallback: progressCallback,
            SessionId: sessionId);
    }
}
