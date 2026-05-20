using System.Text.Json;
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

public class OpenCodeAdapterTests
{
    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new OpenCodeAdapter();

        Assert.Equal("OpenCode", adapter.Name);
        Assert.Contains("opencode", adapter.SupportedProviderTypes);
        Assert.Empty(adapter.ConfigSchema);
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
    public async Task ExecuteAsync_returns_failure_when_binary_not_found()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/nonexistent/opencode",
            executionCount: 1);

        var result = await adapter.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("opencode-error", result.Error);
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
    public async Task ExecuteAsync_reads_binaryPath_from_config_overriding_baseUrl()
    {
        var adapter = new OpenCodeAdapter();

        var ctx = BuildContext(
            binaryPath: "/nonexistent/path",
            config: "{\"binaryPath\":\"/bin/true\"}",
            executionCount: 1);

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
    public async Task ExecuteAsync_passes_worktree_via_dir_argument()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "pwd.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintf '%s\\n' \"$@\"\npwd\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Contains("--dir", result.Output);
            Assert.Contains(worktreeDir, result.Output);
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

            Assert.False(result.Success);
            Assert.Contains("timed out", result.Error);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_streams_extracted_text_from_json_lines_to_progress_callback()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-json-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho '{\"text\":\"thinking...\"}'\necho '{\"text\":\"analyzing code...\"}'\necho '{\"text\":\"done\"}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();
            var progressLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                progressCallback: (line) =>
                {
                    progressLines.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            // Progress lines should be extracted text, not raw JSON
            Assert.Contains("thinking...", progressLines);
            Assert.Contains("analyzing code...", progressLines);
            Assert.Contains("done", progressLines);
            // Verify no raw JSON leaked through
            Assert.DoesNotContain(progressLines, l => l.Contains("{\"text\":"));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_event_type_for_non_text_json_lines()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-event-type-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        // Simulate real opencode JSON events: text, step_start, tool_use, step_finish, empty text
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"step_start\"}'\n" +
            "echo '{\"type\":\"text\",\"text\":\"reading file...\"}'\n" +
            "echo '{\"type\":\"tool_use\",\"tool\":\"read\"}'\n" +
            "echo '{\"type\":\"text\",\"text\":\"\"}'\n" +
            "echo '{\"type\":\"step_finish\"}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();
            var progressLines = new System.Collections.Concurrent.ConcurrentBag<string>();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1,
                progressCallback: (line) =>
                {
                    progressLines.Add(line);
                    return Task.CompletedTask;
                });

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            // Text lines should appear normally
            Assert.Contains("reading file...", progressLines);
            // Step events should show as [step_start] and [step_finish]
            Assert.Contains("[step_start]", progressLines);
            Assert.Contains("[step_finish]", progressLines);
            // Tool use should show as [tool: read]
            Assert.Contains("[tool: read]", progressLines);
            // Empty text lines should NOT appear as blank entries
            Assert.DoesNotContain(string.Empty, progressLines);
            Assert.DoesNotContain(progressLines, l => string.IsNullOrWhiteSpace(l));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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
                prompt: "my prompt",
                worktreePath: worktreeDir,
                sessionId: "test-session-abc",
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Contains("--session", result.Output);
            Assert.Contains("test-session-abc", result.Output);
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
                prompt: "my prompt",
                worktreePath: worktreeDir,
                sessionId: null,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.DoesNotContain("--session", result.Output);
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
            renderedPrompt: "prompt text",
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
    public async Task ExecuteAsync_writes_permission_config_from_tool_allowlist()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-perms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nprintenv OPENCODE_CONFIG_CONTENT\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_treats_prompt_starting_with_dashes_as_message_argument()
    {
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-dashes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        var argsPath = Path.Combine(worktreeDir, "args.txt");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\" > \"$ILD_TEST_ARGS\"\n" +
            "echo '{\"text\":\"ok\",\"sessionId\":\"dash-session\"}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        var previousArgs = Environment.GetEnvironmentVariable("ILD_TEST_ARGS");
        Environment.SetEnvironmentVariable("ILD_TEST_ARGS", argsPath);

        try
        {
            var adapter = new OpenCodeAdapter();
            var result = await adapter.ExecuteAsync(BuildContext(
                binaryPath: scriptPath,
                prompt: "---\nname: to-issues\n",
                worktreePath: worktreeDir,
                executionCount: 1));

            Assert.True(result.Success);
            var args = File.ReadAllLines(argsPath);
            var separatorIndex = Array.IndexOf(args, "--");
            Assert.True(separatorIndex >= 0);
            Assert.Equal(new[] { "---", "name: to-issues", "" }, args.Skip(separatorIndex + 1));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_ARGS", previousArgs);
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
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Equal("session-from-output", result.SessionId);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_returns_session_id_from_nested_session_object()
    {
        // opencode emits stream events whose payloads sometimes nest the
        // session id (`{"session":{"id":"..."}}`) instead of placing it at
        // the root. The extractor must walk the tree so the run can keep
        // its session across executions regardless of the event shape.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho '{\"text\":\"hello\",\"session\":{\"id\":\"nested-session\"}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Equal("nested-session", result.SessionId);
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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

            await using var verifyDb = harness.CreateDbContext();
            var snapshot = await verifyDb.AdapterSessionSnapshots.FindAsync(runId, "OpenCode", "managed-session");
            Assert.NotNull(snapshot);
            Assert.Equal("{\"id\":\"managed-session\",\"messages\":[1]}", snapshot!.SessionJson);

            Assert.Contains("export managed-session", File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_persists_large_managed_session_snapshot_without_truncation()
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
                            text = new string('x', 80_000),
                        },
                    },
                },
            },
        });
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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

            await using var verifyDb = harness.CreateDbContext();
            var snapshot = await verifyDb.AdapterSessionSnapshots.FindAsync(runId, "OpenCode", "large-session");
            Assert.NotNull(snapshot);
            Assert.Equal(exportJson, snapshot!.SessionJson);
            Assert.Equal(exportJson.Length, snapshot.SessionJson.Length);

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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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

            await using var verifyDb = harness.CreateDbContext();
            var snapshot = await verifyDb.AdapterSessionSnapshots.FindAsync(runId, "OpenCode", "managed-session");
            Assert.Null(snapshot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_imports_managed_session_snapshot_before_resuming_run()
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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
            await harness.SeedSnapshotAsync(runId, "OpenCode", "resume-session", "{\"id\":\"resume-session\",\"messages\":[1]}");

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
            Assert.Equal("{\"id\":\"resume-session\",\"messages\":[1]}", File.ReadAllText(importedPath));

            var log = File.ReadAllText(logPath);
            Assert.Contains("import ", log);
            Assert.Contains("run --dir", log);
            Assert.Contains("--session resume-session", log);
            Assert.Contains("export resume-session", log);

            await using var verifyDb = harness.CreateDbContext();
            var snapshot = await verifyDb.AdapterSessionSnapshots.FindAsync(runId, "OpenCode", "resume-session");
            Assert.Equal("{\"id\":\"resume-session\",\"messages\":[2]}", snapshot!.SessionJson);
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
    public async Task ExecuteAsync_starts_fresh_when_stored_managed_session_snapshot_is_invalid_json()
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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
            Assert.DoesNotContain("import ", log);
            Assert.Contains("run --dir", log);
            Assert.DoesNotContain("--session resume-session", log);
            Assert.Contains("export fresh-session", log);

            await using var verifyDb = harness.CreateDbContext();
            var snapshot = await verifyDb.AdapterSessionSnapshots.FindAsync(runId, "OpenCode", "fresh-session");
            Assert.Equal("{\"id\":\"fresh-session\",\"messages\":[1]}", snapshot!.SessionJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_TEST_LOG", previousLog);
            Environment.SetEnvironmentVariable("ILD_TEST_READY", previousReady);
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
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

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
    public async Task ExecuteAsync_extracts_text_from_typed_text_event_part()
    {
        // opencode --format json emits `{"type":"text","part":{"type":"text","text":"..."}}`
        // alongside step_start/step_finish/tool_use events. We must extract the
        // assistant's response from the text part, ignoring boundary events.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-typed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-start\",\"id\":\"prt_1\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}'\n" +
            "echo '{\"type\":\"text\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"text\",\"text\":\"final answer\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}'\n" +
            "echo '{\"type\":\"step_finish\",\"timestamp\":3,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success);
            Assert.Equal("final answer", result.Output);
            Assert.DoesNotContain("step_start", result.Output);
            Assert.DoesNotContain("step_finish", result.Output);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_does_not_dump_raw_ndjson_when_no_text_event_present()
    {
        // Regression: when opencode emits only boundary events (step_start /
        // step_finish / tool_use) without a final text event — e.g. when the
        // model only produced tool calls — we must not surface the raw NDJSON
        // event stream as the AI node output. Surface a clear diagnostic
        // instead so downstream nodes get a readable signal.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-no-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-start\",\"id\":\"prt_1\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}'\n" +
            "echo '{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            // Model produced only tool calls, no assistant text — should be a
            // retryable failure, not a success that passes the error string downstream.
            Assert.False(result.Success);
            Assert.Contains("opencode", result.Error ?? result.Output ?? "");
            Assert.DoesNotContain("step_start", result.Error ?? result.Output ?? "");
            Assert.DoesNotContain("\"type\":", result.Error ?? result.Output ?? "");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_session_error_event_as_failure()
    {
        // opencode emits `{"type":"error","error":{...}}` when a session
        // fails (auth, rate limit, model error, ...). Even when the CLI
        // exits with code 0, we must propagate that error so the AI node
        // reflects the failure instead of silently returning empty output.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-opencode-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "emit.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"error\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"error\":{\"name\":\"ProviderAuthError\",\"data\":{\"message\":\"invalid api key\"}}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var adapter = new OpenCodeAdapter();

            var ctx = BuildContext(
                binaryPath: scriptPath,
                prompt: "ignored",
                worktreePath: worktreeDir,
                executionCount: 1);

            var result = await adapter.ExecuteAsync(ctx);

            Assert.False(result.Success);
            Assert.Contains("invalid api key", result.Error);
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
        bool manageSession = false)
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
            ManageSession: manageSession);
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
                MaxNodeExecutions = 1,
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
