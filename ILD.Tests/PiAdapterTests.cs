using FluentAssertions;
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

public class PiAdapterTests
{
    [Fact]
    public void ConfigSchema_returns_expected_fields()
    {
        var adapter = new PiAdapter();

        adapter.Name.Should().Be("Pi");
        adapter.SupportedProviderTypes.Should().Contain("pi");
        adapter.ConfigSchema.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_binary_not_found()
    {
        var adapter = new PiAdapter();

        var result = await adapter.ExecuteAsync(BuildContext(
            binaryPath: "/nonexistent/pi",
            executionCount: 1));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("pi-error");
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

            result.Success.Should().BeTrue();
            result.Output.Should().Be("hello world");
            result.SessionId.Should().Be("pi-session-123");
            progress.Should().Contain("hello ");
            progress.Should().Contain("world");
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
                    BaseUrl = "http://192.168.1.5:1234/v1",
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

            result.Success.Should().BeTrue();
            result.Output.Should().Be("ok");
            result.Error.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_generates_custom_models_json_for_http_base_url_and_custom_model()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf 'agent-dir=%s\\n' \"$PI_CODING_AGENT_DIR\"\n" +
            "printf 'api-key=%s\\n' \"$ILD_PI_PROVIDER_API_KEY\"\n" +
            "printf '%s\\n' \"$@\"\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Local Default Model",
                Type = "pi",
                BaseUrl = "http://192.168.1.5:1234/v1",
                ApiKey = "sk-local",
                Model = "default_model",
                Config = JsonSerializer.Serialize(new { binaryPath = scriptPath })
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

        try
        {
            result.Success.Should().BeTrue();
            result.Output.Should().Contain("agent-dir=");
            result.Output.Should().Contain("api-key=sk-local");
            result.Output.Should().Contain("--provider");
            result.Output.Should().Contain("ild-11111111111111111111111111111111");
            result.Output.Should().Contain("--model");
            result.Output.Should().Contain("default_model");
            result.Output.Should().NotContain("--api-key");
            result.Output.Should().NotContain("\n--\n");

            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            var modelsJsonPath = Path.Combine(agentDir, "models.json");
            File.Exists(modelsJsonPath).Should().BeTrue();
            var modelsJson = await File.ReadAllTextAsync(modelsJsonPath);
            modelsJson.Should().Contain("http://192.168.1.5:1234/v1");
            modelsJson.Should().Contain("default_model");
            modelsJson.Should().Contain("openai-completions");
            modelsJson.Should().Contain("ILD_PI_PROVIDER_API_KEY");
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

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("--mode");
            result.Output.Should().Contain("json");
            result.Output.Should().Contain("--session-dir");
            result.Output.Should().Contain("--session");
            result.Output.Should().Contain("openai/gpt-5");
            result.Output.Should().Contain("sk-test");
            result.Output.Should().NotContain("\n--\n");
            result.Output.Should().Contain("STDIN-BEGIN");
            result.Output.Should().Contain("my prompt");
            result.Output.Should().Contain("STDIN-END");
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

            result.Success.Should().BeTrue();
            var restoredSessionPath = Path.Combine(Path.GetTempPath(), "ild-pi-sessions", runId.ToString("N"), "pi-session-restore.jsonl");
            File.Exists(restoredSessionPath).Should().BeTrue();
            (await File.ReadAllTextAsync(restoredSessionPath)).Should().Contain("pi-session-restore");
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
            "printf '%s\\n' \"$@\"\n");
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

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("--session");
            result.Output.Should().Contain(actualSessionPath);
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

            result.Success.Should().BeTrue();
            result.Output.Should().NotContain("Unknown option");
            result.Output.Should().Contain("STDIN-BEGIN");
            result.Output.Should().Contain("---");
            result.Output.Should().Contain("name: to-issues");
            result.Output.Should().Contain("STDIN-END");
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_writes_ild_extension_to_agent_directory_when_http_base_url()
    {
        var runId = Guid.NewGuid();
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-pi-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "args.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "printf 'agent-dir=%s\\n' \"$PI_CODING_AGENT_DIR\"\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-ext\",\"cwd\":\"$PWD\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        System.Diagnostics.Process.Start("chmod", "+x " + scriptPath).WaitForExit();

        try
        {
            var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Name = "test-provider",
                    Type = "pi",
                    BaseUrl = "http://192.168.1.5:1234/v1",
                    ApiKey = "sk-local",
                    Model = "default_model",
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

            result.Success.Should().BeTrue();

            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            var extensionPath = Path.Combine(agentDir, "extensions", "ild.ts");
            File.Exists(extensionPath).Should().BeTrue("extension file should be written to agent directory/extensions");

            var extensionContent = await File.ReadAllTextAsync(extensionPath);
            extensionContent.Should().Contain("ild_list_workitems");
            extensionContent.Should().Contain("ild_get_workitem");
            extensionContent.Should().Contain("ild_create_workitem");
            extensionContent.Should().Contain("ild_list_repositories");
            extensionContent.Should().Contain("ild_list_loop_templates");
            extensionContent.Should().Contain("ild_list_loop_runs");
            extensionContent.Should().Contain("http://192.168.1.5:1234/v1");
            extensionContent.Should().Contain("sk-local");
            extensionContent.Should().Contain(runId.ToString());
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

            result.Success.Should().BeTrue();

            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            Directory.Exists(agentDir).Should().BeFalse("agent directory should not be created when no HTTP base URL");
        }
        finally
        {
            var agentDir = Path.Combine(Path.GetTempPath(), "ild-pi-agent", runId.ToString("N"));
            if (Directory.Exists(agentDir))
                Directory.Delete(agentDir, true);
            Directory.Delete(worktreeDir, true);
        }
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
        bool manageSession = false)
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
                MaxWallClockHours = 1,
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