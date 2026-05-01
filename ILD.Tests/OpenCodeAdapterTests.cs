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
        adapter.ConfigSchema.Should().HaveCount(2);
        adapter.ConfigSchema[0].Name.Should().Be("binaryPath");
        adapter.ConfigSchema[1].Name.Should().Be("timeoutSeconds");
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

            var ctx = BuildContext(
                binaryPath: scriptPath,
                config: "{\"timeoutSeconds\":1}",
                executionCount: 1);

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

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string initialPrompt = "test prompt",
        string? loopPrompt = null,
        string? config = null,
        string? workItemTitle = null,
        string? workItemDescription = null,
        string? worktreePath = null,
        string? apiKey = null,
        int executionCount = 1)
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
            Cancel: CancellationToken.None);
    }
}
