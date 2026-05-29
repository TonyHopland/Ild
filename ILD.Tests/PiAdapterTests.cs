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

        Assert.Equal("Pi", adapter.Name);
        Assert.Contains("pi", adapter.SupportedProviderTypes);
        Assert.Empty(adapter.ConfigSchema);
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
            Assert.Contains("read,grep,find,ls,edit,write,bash,ild_list_workitems,ild_get_workitem,ild_create_workitem,ild_list_repositories,ild_list_loop_templates,ild_list_loop_runs", result.Output);
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
    public void ExecuteAsync_escapes_control_characters_in_ild_extension_strings()
    {
        var extensionContent = GeneratePiExtension(
            "http://localhost:1234/v1",
            "sk-local\r\nnext-line",
            Guid.NewGuid().ToString());

        Assert.Contains("const API_TOKEN = \"sk-local\\r\\nnext-line\";", extensionContent);
        Assert.DoesNotContain("sk-local\r\nnext-line", extensionContent);
    }

    [Fact]
    public void ExecuteAsync_generates_query_parameter_code_without_embedded_newlines()
    {
        var extensionContent = GeneratePiExtension(
            "http://localhost:1234/v1",
            "Local",
            Guid.NewGuid().ToString());

        Assert.Contains("function joinApiUrl(base: string, path: string): string {", extensionContent);
        Assert.Contains("const url = joinApiUrl(API_BASE, path);", extensionContent);
        Assert.DoesNotContain("const url = API_BASE + path;", extensionContent);
        Assert.Contains("if (params.status != null) qs.set(\"status\", params.status);", extensionContent);
        Assert.Contains("if (params.skip !== undefined) qs.set(\"skip\", String(params.skip));", extensionContent);
        Assert.Contains("const url = qs.toString() ? `api/v1/agent/workitems?${qs.toString()}` : \"api/v1/agent/workitems\";", extensionContent);
        Assert.DoesNotContain("qs.set(\"\nstatus", extensionContent);
    }

    [Fact]
    public void ExecuteAsync_filters_ild_extension_tools_when_requested()
    {
        var generatorType = typeof(PiAdapter).Assembly
            .GetType("ILD.Core.Services.Implementations.Adapters.PiExtensionGenerator", throwOnError: true)!;
        var generateMethod = generatorType.GetMethod(
            "Generate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string), typeof(IReadOnlyCollection<string>)],
            modifiers: null)!;

        var extensionContent = (string)generateMethod.Invoke(null, [
            "http://localhost:1234/v1",
            "Local",
            Guid.NewGuid().ToString(),
            new[] { "ild_get_workitem" }
        ])!;

        Assert.Contains("ild_get_workitem", extensionContent);
        Assert.DoesNotContain("ild_list_workitems", extensionContent);
        Assert.DoesNotContain("ild_create_workitem", extensionContent);
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

    private static string GeneratePiExtension(string apiUrl, string apiToken, string loopRunId)
    {
        var generatorType = typeof(PiAdapter).Assembly
            .GetType("ILD.Core.Services.Implementations.Adapters.PiExtensionGenerator", throwOnError: true)!;
        var generateMethod = generatorType.GetMethod(
            "Generate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string)],
            modifiers: null)!;

        return (string)generateMethod.Invoke(null, [apiUrl, apiToken, loopRunId])!;
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