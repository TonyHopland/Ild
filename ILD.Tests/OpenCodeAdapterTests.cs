using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

namespace ILD.Tests;

[SupportedOSPlatform("linux")]
public class OpenCodeAdapterTests
{
    /// <summary>
    /// Mark a generated stub script as executable. Uses
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> so a path
    /// with spaces is passed as a single argument (no shell-style quoting), and
    /// guards against <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
    /// returning null.
    /// </summary>
    private static void MakeExecutable(string scriptPath)
    {
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new OpenCodeAdapter();

        Assert.Equal("OpenCode", adapter.Name);
        Assert.Contains("opencode", adapter.SupportedProviderTypes);
        Assert.Contains(adapter.ConfigSchema, f => f.Name == "customMcpServersJson");
        // opencode folds the model into its generated config and always passes
        // --model, so blanking it stays a validation error.
        Assert.Equal(AdapterModelSupport.Required, adapter.ModelSupport);
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_binary_exits_zero()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_uses_prompt_for_all_executions()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            prompt: "shared prompt here",
            executionCount: 2);

        var result = await adapter.ExecuteAsync(ctx);

        Assert.True(result.Success);
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

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_returns_stderr_on_nonzero_exit()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/sh",
            prompt: "-c exit 42",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("exit=", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_renders_work_item_placeholders()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/bin/true",
            prompt: "Fix: {{WorkItem.Title}} - {{WorkItem.Description}}",
            workItemTitle: "Null reference in parser",
            workItemDescription: "Fix the crash",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        Assert.True(result.Success);
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
                prompt: "Read: {{WorkTree.File:test.txt}}",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
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
        File.WriteAllText(scriptPath, "#!/bin/sh\necho started\nsleep 60\n");
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            using var cts = new System.Threading.CancellationTokenSource();

            // Cancel once the process is known to be running, not after a wall-clock wait.
            var ctx = BuildContext(
                binaryPath: scriptPath,
                executionCount: 1,
                cancel: cts.Token,
                progressCallback: _ =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("timed out", result.Error);
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
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            var progressLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                executionCount: 1,
                progressCallback: (line) =>
                {
                    progressLines.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Contains("line one", progressLines);
            Assert.Contains("line two", progressLines);
            Assert.Contains("line three", progressLines);
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
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                apiKey: "test-secret-key-123",
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Contains("test-secret-key-123", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Theory]
    [InlineData("test-provider", "test-provider/test-model")]
    [InlineData("OpenCode", "opencode/test-model")]
    public async Task ExecuteAsync_points_the_model_and_the_small_model_at_the_configured_model(string providerName, string modelRef)
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "env.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        MakeExecutable(scriptPath);

        try
        {
            var ctx = BuildContext(binaryPath: scriptPath, prompt: "ignored", worktreePath: worktreeDir, executionCount: 1);
            ctx.Provider.Name = providerName;

            var result = await new OpenCodeAdapter().ExecuteAsync(ctx);

            Assert.True(result.Success);
            using var config = JsonDocument.Parse(Assert.IsType<string>(result.Output));
            Assert.Equal(modelRef, config.RootElement.GetProperty("model").GetString());
            Assert.Equal(modelRef, config.RootElement.GetProperty("small_model").GetString());
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public void BuildRunProcessStartInfo_includes_full_command_and_config_for_retry()
    {
        var psi = OpenCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "opencode",
            worktreePath: "/tmp/worktree",
            prompt: "prompt text",
            opencodeModel: "provider/model",
            opencodeConfigJson: "{\"config\":true}",
            sessionId: "session-123",
            useWorktreeAsWorkingDirectory: false);

        Assert.True(string.IsNullOrEmpty(psi.WorkingDirectory));
        Assert.Equal("{\"config\":true}", psi.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal(new[]
        {
            "run",
            "--dir",
            "/tmp/worktree",
            "--model",
            "provider/model",
            "--format",
            "json",
            "--session",
            "session-123",
            "--",
            "prompt text",
        }, psi.ArgumentList);
    }

    [Fact]
    public async Task ExecuteAsync_gives_the_extra_arguments_to_the_run_but_not_to_session_import_or_export()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-extra-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "opencode.sh");
        // Like a fresh opencode: the session is not local until it is imported, so
        // export only has it once the run has happened.
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" > '{worktreeDir}/argv-'\"$1\".txt\n" +
            "case \"$1\" in\n" +
            "  run) echo '{\"text\":\"hello\",\"sessionId\":\"managed-session\"}' ;;\n" +
            $"  export) [ -f '{worktreeDir}/argv-run.txt' ] && printf '%s' '{{\"id\":\"managed-session\",\"messages\":[2]}}' ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "managed-session", "{\"id\":\"managed-session\",\"messages\":[1]}");

            var result = await new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "do it",
                config: JsonSerializer.Serialize(new { extraArgs = "--variant high --title \"a b\"" }),
                worktreePath: worktreeDir,
                sessionId: "managed-session",
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success, result.Error);
            var run = File.ReadAllLines(Path.Combine(worktreeDir, "argv-run.txt"));
            Assert.Equal(["--variant", "high", "--title", "a b", "--", "do it"], run[^6..]);
            var firstExtra = run.Length - 6;
            foreach (var ildFlag in new[] { "--dir", "--model", "--format", "--session" })
                Assert.InRange(Array.IndexOf(run, ildFlag), 0, firstExtra - 1);

            var import = File.ReadAllLines(Path.Combine(worktreeDir, "argv-import.txt"));
            Assert.Equal("import", import[0]);
            Assert.Equal(2, import.Length);
            Assert.Equal(["export", "managed-session"], File.ReadAllLines(Path.Combine(worktreeDir, "argv-export.txt")));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public void BuildRunProcessStartInfo_for_the_retry_without_a_working_directory_keeps_the_extra_arguments_before_the_prompt()
    {
        var psi = OpenCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "opencode",
            worktreePath: "/tmp/worktree",
            prompt: "prompt text",
            opencodeModel: "provider/model",
            opencodeConfigJson: "{}",
            sessionId: "session-123",
            useWorktreeAsWorkingDirectory: false,
            extraArgs: ["--variant", "high"]);

        Assert.True(string.IsNullOrEmpty(psi.WorkingDirectory));
        Assert.Equal(new[]
        {
            "run",
            "--dir",
            "/tmp/worktree",
            "--model",
            "provider/model",
            "--format",
            "json",
            "--session",
            "session-123",
            "--variant",
            "high",
            "--",
            "prompt text",
        }, psi.ArgumentList);
    }

    [Fact]
    public async Task ExecuteAsync_writes_permission_config_from_tool_allowlist()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-perms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            var result = await adapter.ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "Task", "Desc", worktreeDir, "main", new List<string>(), null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None,
                ToolAllowlist: ["read", "ild"]));

            Assert.True(result.Success);
            Assert.Contains("\"read\":\"allow\"", result.Output);
            Assert.Contains("\"grep\":\"allow\"", result.Output);
            Assert.Contains("\"edit\":\"deny\"", result.Output);
            Assert.Contains("\"bash\":\"deny\"", result.Output);
            Assert.Contains("\"mcp\"", result.Output);
            // No extra allowed dirs ⇒ external directory access stays denied.
            Assert.Contains("\"external_directory\":\"deny\"", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_injects_provider_custom_mcp_servers_even_without_ild()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            var result = await adapter.ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new
                    {
                        binaryPath = scriptPath,
                        customMcpServersJson = """
                        { "chrome-devtools": { "command": ["npx", "-y", "chrome-devtools-mcp@latest", "--headless"] } }
                        """,
                    }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "Task", "Desc", worktreeDir, "main", new List<string>(), null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None,
                // Only "read" — no "ild". The custom server must still be injected.
                ToolAllowlist: ["read"]));

            Assert.True(result.Success);
            Assert.Contains("\"chrome-devtools\"", result.Output);
            Assert.Contains("chrome-devtools-mcp@latest", result.Output);
            Assert.Contains("\"type\":\"local\"", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_under_NoTools_injects_no_mcp_server_and_denies_every_tool_whatever_the_allowlist()
    {
        // The flag off is the test above: the custom server is injected.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-notools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        MakeExecutable(scriptPath);

        try
        {
            var result = await new OpenCodeAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new
                    {
                        binaryPath = scriptPath,
                        customMcpServersJson = """
                        { "chrome-devtools": { "command": ["npx", "-y", "chrome-devtools-mcp@latest", "--headless"] } }
                        """,
                    }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), string.Empty, string.Empty, string.Empty, worktreeDir, string.Empty, new List<string>(), null),
                ExecutionCount: 0,
                Cancel: CancellationToken.None,
                ToolAllowlist: ["read", "write", "execute", "ild"],
                AdditionalAllowedDirectories: ["/data/worktrees/wi-99"],
                NoTools: true));

            Assert.True(result.Success, result.Error);
            using var config = JsonDocument.Parse(result.Output!);
            Assert.False(config.RootElement.TryGetProperty("mcp", out _));
            var permissions = config.RootElement.GetProperty("permission").EnumerateObject().ToList();
            Assert.All(permissions, permission => Assert.Equal("deny", permission.Value.GetString()));
            // opencode applies the last matching rule, so a wildcard deny first leaves no
            // unnamed tool (todowrite, among others) on its default.
            Assert.Equal("*", permissions[0].Name);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    public enum PlainCallEnd { Answers, Fails, TimesOut }

    [Theory]
    [InlineData(PlainCallEnd.Answers, true)]
    [InlineData(PlainCallEnd.Fails, true)]
    [InlineData(PlainCallEnd.TimesOut, true)]
    [InlineData(PlainCallEnd.Answers, false)]
    public async Task ExecuteAsync_deletes_the_session_of_a_NoTools_call_however_it_ends_and_no_other(PlainCallEnd end, bool noTools)
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-plain-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var deleted = Path.Combine(worktreeDir, "deleted");
        var scriptPath = Path.Combine(worktreeDir, "opencode.sh");
        var ending = end switch
        {
            PlainCallEnd.Answers => "echo '{\"type\":\"text\",\"sessionID\":\"ses_plain\",\"part\":{\"type\":\"text\",\"text\":\"Login page fix\"}}'\n",
            PlainCallEnd.Fails => "exit 3\n",
            _ => "exec sleep 30\n",
        };
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"if [ \"$1\" = session ]; then echo \"$2 $3\" >> '{deleted}'; exit 0; fi\n" +
            "echo '{\"type\":\"step_start\",\"sessionID\":\"ses_plain\"}'\n" +
            ending);
        MakeExecutable(scriptPath);
        // The call is given up on once its session exists, the way a timeout ends one,
        // with no clock to race the stand-in's first line.
        using var timeout = new CancellationTokenSource();

        try
        {
            var result = await new OpenCodeAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), string.Empty, string.Empty, string.Empty, worktreeDir, string.Empty, new List<string>(), null),
                ExecutionCount: 0,
                Cancel: timeout.Token,
                OnSessionId: end == PlainCallEnd.TimesOut ? _ => timeout.Cancel() : null,
                ToolAllowlist: ["read"],
                NoTools: noTools));

            Assert.Equal(end == PlainCallEnd.Answers, result.Success);
            if (noTools)
                Assert.Equal(["delete ses_plain"], File.ReadAllLines(deleted));
            else
                Assert.False(File.Exists(deleted));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_deletes_the_session_of_a_NoTools_call_whose_id_was_printed_but_unread_at_the_timeout()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-unread-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var deleted = Path.Combine(worktreeDir, "deleted");
        var go = Path.Combine(worktreeDir, "go");
        using (var mkfifo = Process.Start("mkfifo", go)) mkfifo.WaitForExit();
        var scriptPath = Path.Combine(worktreeDir, "opencode.sh");
        // The id line is written only once the reader has taken the first line and is
        // held in the progress callback, so it is still unread when the call times out.
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"if [ \"$1\" = session ]; then echo \"$2 $3\" >> '{deleted}'; exit 0; fi\n" +
            "echo '{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"first\"}}'\n" +
            $"read released < '{go}'\n" +
            "echo '{\"type\":\"step_start\",\"sessionID\":\"ses_unread\"}'\n" +
            "echo '{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"id printed\"}}' >&2\n" +
            "exec sleep 30\n");
        MakeExecutable(scriptPath);
        using var timeout = new CancellationTokenSource();
        var idPrinted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var result = await new OpenCodeAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), string.Empty, string.Empty, string.Empty, worktreeDir, string.Empty, new List<string>(), null),
                ExecutionCount: 0,
                Cancel: timeout.Token,
                ProgressCallback: async text =>
                {
                    if (text.Contains("id printed"))
                    {
                        idPrinted.TrySetResult();
                    }
                    else if (text.Contains("first"))
                    {
                        await File.WriteAllTextAsync(go, "go\n");
                        await idPrinted.Task;
                        timeout.Cancel();
                    }
                },
                ToolAllowlist: ["read"],
                NoTools: true));

            Assert.False(result.Success);
            Assert.Equal(["delete ses_unread"], File.ReadAllLines(deleted));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_allows_external_directory_when_extra_dirs_granted()
    {
        // ADR-0011 parity: opencode's equivalent of claude's --add-dir is flipping
        // the external_directory permission so the Chat Context's worktree path
        // (outside the agent's --dir cwd) becomes reachable.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-extdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        MakeExecutable(scriptPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            var result = await adapter.ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = "test-provider",
                    Type = "opencode",
                    BaseUrl = "https://api.example.test/v1",
                    Model = "gpt-5",
                    Config = JsonSerializer.Serialize(new { binaryPath = scriptPath }),
                },
                Prompt: "prompt",
                RunContext: new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "Task", "Desc", worktreeDir, "main", new List<string>(), null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None,
                ToolAllowlist: ["read", "ild"],
                AdditionalAllowedDirectories: new[] { "/data/worktrees/wi-99" }));

            Assert.True(result.Success);
            Assert.Contains("\"external_directory\":\"allow\"", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_invokes_OnSessionId_once_with_first_session_id()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-sid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        // Two events carry the session id; the callback must fire only once.
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"step_start\",\"sessionId\":\"oc-live\"}'\n" +
            "echo '{\"type\":\"text\",\"text\":\"hello\",\"sessionId\":\"oc-live\"}'\n");
        MakeExecutable(scriptPath);

        try
        {
            var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                onSessionId: sid => captured.Add(sid));

            var result = await new OpenCodeAdapter().ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Single(captured);
            Assert.Equal("oc-live", captured.Single());
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_exports_managed_session_snapshot_after_successful_run()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    echo '{\"text\":\"hello\",\"sessionId\":\"managed-session\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    printf '%s' '{\"id\":\"managed-session\",\"messages\":[1]}'\n" +
            "    ;;\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);

            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("managed-session", result.SessionId);

            Assert.Equal("opencode-json", (await harness.SnapshotMetadataAsync(runId, "managed-session"))?.Format);
            Assert.Equal("{\"id\":\"managed-session\",\"messages\":[1]}", Encoding.UTF8.GetString((await harness.SnapshotContentAsync(runId, "managed-session"))!));

            Assert.Contains("export managed-session", File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_persists_large_managed_session_snapshot_byte_for_byte()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        var exportPath = Path.Combine(worktreeDir, "session.json");
        var exportJson = JsonSerializer.Serialize(new
        {
            id = "large-session",
            messages = new[]
            {
                new
                {
                    role = "assistant",
                    parts = new[]
                    {
                        new
                        {
                            type = "text",
                            text = new string('x', 80_000) + " café ✓ 🙂",
                        },
                    },
                },
            },
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(exportPath, exportJson);
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    echo '{\"text\":\"hello\",\"sessionId\":\"large-session\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    cat \"$ILD_TEST_EXPORT\"\n" +
            "    ;;\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        var previousExport = Environment.GetEnvironmentVariable("ILD_TEST_EXPORT");
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);
        Environment.SetEnvironmentVariable("ILD_TEST_EXPORT", exportPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);

            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("large-session", result.SessionId);

            var exportBytes = File.ReadAllBytes(exportPath);
            var metadata = await harness.SnapshotMetadataAsync(runId, "large-session");
            Assert.Equal("opencode-json", metadata?.Format);
            Assert.Equal(exportBytes.LongLength, metadata!.ByteLength);
            Assert.Equal(exportBytes, await harness.SnapshotContentAsync(runId, "large-session"));

            Assert.Contains("export large-session", File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Environment.SetEnvironmentVariable("ILD_TEST_EXPORT", previousExport);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_keeps_successful_output_when_managed_session_export_returns_invalid_json()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    echo '{\"text\":\"hello\",\"sessionId\":\"managed-session\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    printf '%s' '{\"id\":\"managed-session\",\"messages\":[{\"role\":\"assistant\",\"parts\":[{\"type\":\"text\",\"text\":\"unterminated\"}]}'\n" +
            "    ;;\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);

            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("hello", result.Output);
            Assert.Equal("managed-session", result.SessionId);

            Assert.Null(await harness.SnapshotMetadataAsync(runId, "managed-session"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_imports_the_stored_session_when_the_local_one_is_missing()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        var importedPath = Path.Combine(worktreeDir, "imported.json");
        var readyPath = Path.Combine(worktreeDir, "session-ready");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    cat \"$1\" > \"$ILD_TEST_IMPORTED\"\n" +
            "    ;;\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    touch \"$ILD_TEST_READY\"\n" +
            "    echo '{\"text\":\"still here\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    if [ ! -f \"$ILD_TEST_READY\" ]; then exit 1; fi\n" +
            "    printf '%s' '{\"id\":\"resume-session\",\"messages\":[2]}'\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        var previousImported = Environment.GetEnvironmentVariable("ILD_TEST_IMPORTED");
        var previousReady = Environment.GetEnvironmentVariable("ILD_TEST_READY");
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);
        Environment.SetEnvironmentVariable("ILD_TEST_IMPORTED", importedPath);
        Environment.SetEnvironmentVariable("ILD_TEST_READY", readyPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "resume-session", "{\"id\":\"resume-session\",\"messages\":[\"café ✓ 🙂\"]}");
            var stored = await harness.SnapshotContentAsync(runId, "resume-session");

            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                sessionId: "resume-session",
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("resume-session", result.SessionId);
            Assert.Equal(stored, File.ReadAllBytes(importedPath));

            var log = File.ReadAllLines(logPath);
            var importedFile = Assert.Single(log, line => line.StartsWith("import ", StringComparison.Ordinal))["import ".Length..];
            Assert.False(File.Exists(importedFile), "the import temp file is deleted afterwards");
            Assert.Contains(log, line => line.StartsWith("run --dir", StringComparison.Ordinal) && line.Contains("--session resume-session"));
            Assert.Contains("export resume-session", log);

            Assert.Equal("{\"id\":\"resume-session\",\"messages\":[2]}", Encoding.UTF8.GetString((await harness.SnapshotContentAsync(runId, "resume-session"))!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Environment.SetEnvironmentVariable("ILD_TEST_IMPORTED", previousImported);
            Environment.SetEnvironmentVariable("ILD_TEST_READY", previousReady);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_starts_fresh_when_the_stored_session_fails_to_import()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        var readyPath = Path.Combine(worktreeDir, "session-ready");
        var previousReady = Environment.GetEnvironmentVariable("ILD_TEST_READY");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    exit 99\n" +
            "    ;;\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    touch \"$ILD_TEST_READY\"\n" +
            "    echo '{\"text\":\"fresh start\",\"sessionId\":\"fresh-session\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    if [ ! -f \"$ILD_TEST_READY\" ]; then exit 1; fi\n" +
            "    printf '%s' '{\"id\":\"fresh-session\",\"messages\":[1]}'\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);
        Environment.SetEnvironmentVariable("ILD_TEST_READY", readyPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "resume-session", "{\"info\":\"truncated");

            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                sessionId: "resume-session",
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("fresh-session", result.SessionId);

            var log = File.ReadAllText(logPath);
            Assert.Contains("import ", log);
            Assert.Contains("run --dir", log);
            Assert.DoesNotContain("--session resume-session", log);
            Assert.Contains("export fresh-session", log);

            Assert.Equal("{\"id\":\"fresh-session\",\"messages\":[1]}", Encoding.UTF8.GetString((await harness.SnapshotContentAsync(runId, "fresh-session"))!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Environment.SetEnvironmentVariable("ILD_TEST_READY", previousReady);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_never_imports_a_stored_session_of_another_format()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    ;;\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    echo '{\"text\":\"hello\"}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    exit 1\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "resume-session", "{\"id\":\"resume-session\",\"messages\":[1]}", format: "raw");

            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                sessionId: "resume-session",
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Contains("run ", File.ReadAllText(logPath));
            Assert.DoesNotContain("import ", File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_recovers_response_from_exported_managed_session_when_stdout_has_no_text()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "managed.sh");
        var logPath = Path.Combine(worktreeDir, "opencode.log");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "cmd=\"$1\"\n" +
            "shift\n" +
            "case \"$cmd\" in\n" +
            "  import)\n" +
            "    printf 'import %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    ;;\n" +
            "  run)\n" +
            "    printf 'run %s\\n' \"$*\" >> \"$ILD_TEST_LOG\"\n" +
            "    echo '{\"type\":\"step_start\",\"sessionID\":\"resume-session\",\"part\":{\"type\":\"step-start\"}}'\n" +
            "    echo '{\"type\":\"step_finish\",\"sessionID\":\"resume-session\",\"part\":{\"type\":\"step-finish\"}}'\n" +
            "    ;;\n" +
            "  export)\n" +
            "    printf 'export %s\\n' \"$1\" >> \"$ILD_TEST_LOG\"\n" +
            "    printf '%s' '{\"id\":\"resume-session\",\"messages\":[{\"role\":\"assistant\",\"parts\":[{\"type\":\"reasoning\",\"text\":\"hidden\"},{\"type\":\"text\",\"text\":\"Recovered from session export.\"}]}]}'\n" +
            "    ;;\n" +
            "esac\n");
        MakeExecutable(scriptPath);

        var previousLog = Environment.GetEnvironmentVariable("ILD_TEST_LOG");
        Environment.SetEnvironmentVariable("ILD_TEST_LOG", logPath);

        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "resume-session", "{\"id\":\"resume-session\",\"messages\":[]}");

            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                sessionId: "resume-session",
                executionCount: 1,
                runId: runId,
                manageSession: true));

            Assert.True(result.Success);
            Assert.Equal("Recovered from session export.", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_forks_source_snapshot_under_new_id_leaving_source_unchanged()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "fork.sh");
        // Minimal opencode stub: succeed with no output for every subcommand
        // (run/export/import), so the only snapshot writes come from the fork.
        File.WriteAllText(scriptPath, "#!/bin/sh\nexit 0\n");
        MakeExecutable(scriptPath);

        const string sourceJson = "{\"id\":\"source-sess\",\"messages\":[1]}";
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "OpenCode", "source-sess", sourceJson);

            var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                sessionId: "fork-dest",
                executionCount: 1,
                runId: runId,
                manageSession: true) with { ForkFromSessionId = "source-sess" };

            await adapter.ExecuteAsync(ctx);

            Assert.Equal(sourceJson, Encoding.UTF8.GetString((await harness.SnapshotContentAsync(runId, "source-sess"))!));
            var fork = Encoding.UTF8.GetString((await harness.SnapshotContentAsync(runId, "fork-dest"))!);
            Assert.Contains("fork-dest", fork);
            Assert.DoesNotContain("source-sess", fork);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string prompt = "test prompt",
        string? config = null,
        string? workItemTitle = null,
        string? workItemDescription = null,
        string? worktreePath = null,
        string? apiKey = null,
        int executionCount = 1,
        CancellationToken? cancel = null,
        Func<string, Task>? progressCallback = null,
        string? sessionId = null,
        Guid? runId = null,
        bool manageSession = false,
        Action<string>? onSessionId = null)
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
            Prompt: prompt,
            RunContext: new LoopRunContext(
                runId ?? Guid.NewGuid(),
                Guid.NewGuid().ToString(),
                workItemTitle ?? "Test Task",
                workItemDescription ?? "Test description",
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

        public async Task SeedSnapshotAsync(Guid runId, string adapterName, string sessionId, string content, string format = "opencode-json")
        {
            await using var scope = _provider.CreateAsyncScope();
            await SessionSnapshotTestSupport.SeedAsync(
                scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>(),
                SessionSnapshotKey.ForRun(runId, adapterName, sessionId), format, content, CancellationToken.None);
        }

        public async Task<SessionSnapshotMetadata?> SnapshotMetadataAsync(Guid runId, string sessionId)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>()
                .GetMetadataAsync(SessionSnapshotKey.ForRun(runId, "OpenCode", sessionId), CancellationToken.None);
        }

        public async Task<byte[]?> SnapshotContentAsync(Guid runId, string sessionId)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await SessionSnapshotTestSupport.ReadAllAsync(
                scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>(),
                SessionSnapshotKey.ForRun(runId, "OpenCode", sessionId), CancellationToken.None);
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
