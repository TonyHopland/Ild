using ILD.Core.Services.Implementations.Adapters;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// Fork coverage for the claude-code adapter. The adapter is given its own
/// <c>HOME</c>, because claude stores session JSONL under <c>$HOME/.claude/projects</c>.
/// </summary>
public class ClaudeCodeAdapterForkTests
{
    [Fact]
    public async Task ExecuteAsync_forks_source_snapshot_under_new_id_leaving_source_unchanged()
    {
        var home = Path.Combine(Path.GetTempPath(), $"ild-claude-fork-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-claude-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);

        var sourceJson = ClaudeCodeAdapter.WrapJsonl(
            "source-sess",
            "{\"session_id\":\"source-sess\",\"type\":\"assistant\",\"text\":\"hi\"}\n");

        var environment = new TestProcessEnvironment { { "HOME", home } };
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "ClaudeCode", "source-sess", sourceJson);

            var adapter = new ClaudeCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>(), environment: environment);
            var ctx = BuildContext(
                binaryPath: "/bin/true",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "fork-dest",
                manageSession: true) with { ForkFromSessionId = "source-sess" };

            await adapter.ExecuteAsync(ctx);

            await using var verifyDb = harness.CreateDbContext();
            // Source session is byte-for-byte unchanged after the fork.
            var source = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "ClaudeCode" && s.SessionId == "source-sess", TestContext.Current.CancellationToken);
            Assert.NotNull(source);
            Assert.Equal(sourceJson, source!.SessionJson);
            // A copy now exists under the fork's id, retargeted to that id.
            var fork = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "ClaudeCode" && s.SessionId == "fork-dest", TestContext.Current.CancellationToken);
            Assert.NotNull(fork);
            Assert.Contains("fork-dest", fork!.SessionJson);
            Assert.DoesNotContain("source-sess", fork.SessionJson);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_records_only_the_forks_own_cost_when_the_copy_carries_the_parents_cost_state()
    {
        var home = Directory.CreateTempSubdirectory("ild-claude-fork-cost-home-").FullName;
        var worktreeDir = Directory.CreateTempSubdirectory("ild-claude-fork-cost-").FullName;
        var scriptPath = Path.Combine(worktreeDir, "fake-claude.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"fork-dest\"}'\n" +
            "echo '{\"type\":\"assistant\",\"session_id\":\"fork-dest\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}'\n" +
            "echo '{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"fork-dest\",\"is_error\":false,\"result\":\"ok\",\"total_cost_usd\":1.25,\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}'\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sourceJson = ClaudeCodeAdapter.WrapJsonl(
            "source-sess",
            "{\"type\":\"user\",\"sessionId\":\"source-sess\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}\n" +
            "{\"type\":\"cost-state\",\"sessionId\":\"source-sess\",\"totalCostUSD\":1.00,\"totalAPIDuration\":11774,\"modelUsage\":{}}\n");

        var environment = new TestProcessEnvironment { { "HOME", home } };
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "ClaudeCode", "source-sess", sourceJson);

            var adapter = new ClaudeCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>(), environment: environment);
            var ctx = BuildContext(
                binaryPath: scriptPath,
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "fork-dest",
                manageSession: true) with { ForkFromSessionId = "source-sess" };

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.Usage);
            Assert.Equal(0.25m, result.Usage!.CostUsd);
            Assert.Equal(10, result.Usage.InputTokens);
            Assert.Equal(4, result.Usage.OutputTokens);
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
            Directory.Delete(home, true);
        }
    }

    private static AgentExecutionContext BuildContext(
        string binaryPath,
        string worktreePath,
        Guid runId,
        string sessionId,
        bool manageSession)
        => new(
            Provider: new AiProvider
            {
                Name = "claude-test",
                Type = "claude-code",
                BaseUrl = string.Empty,
                ApiKey = null,
                Model = string.Empty,
                Config = $"{{\"binaryPath\":\"{binaryPath}\"}}",
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(
                runId,
                Guid.NewGuid().ToString(),
                "Test Task",
                "Test description",
                worktreePath,
                "main",
                new List<string>(),
                null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            SessionId: sessionId,
            ManageSession: manageSession);

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
