using System.Runtime.Versioning;
using ILD.Core.Services.Implementations;
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
[SupportedOSPlatform("linux")]
public class ClaudeCodeAdapterForkTests
{
    [Fact]
    public async Task ExecuteAsync_forks_source_snapshot_under_new_id_leaving_source_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("ild-claude-fork-home-").FullName;
        var worktreeDir = Directory.CreateTempSubdirectory("ild-claude-fork-").FullName;
        const string sourceJsonl =
            "{\"sessionId\":\"source-sess\",\"type\":\"user\",\"text\":\"go\"}\n" +
            "{\"sessionId\":\"source-sess\",\"type\":\"assistant\",\"text\":\"hi\"}\n";

        var environment = new TestProcessEnvironment { { "HOME", home } };
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            var sourceKey = SessionSnapshotKey.ForRun(runId, "ClaudeCode", "source-sess");
            var destKey = SessionSnapshotKey.ForRun(runId, "ClaudeCode", "fork-dest");
            await harness.WithStoreAsync(store => SessionSnapshotTestSupport.SeedAsync(store, sourceKey, "claude-jsonl", sourceJsonl, ct));
            var sourceBefore = await harness.WithStoreAsync(store => store.GetMetadataAsync(sourceKey, ct));
            var sourceChunksBefore = await harness.ChunkDataAsync(sourceBefore!.Id);

            var adapter = new ClaudeCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>(), environment: environment);
            var ctx = BuildContext(
                binaryPath: "/bin/true",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "fork-dest",
                manageSession: true) with { ForkFromSessionId = "source-sess" };

            var result = await adapter.ExecuteAsync(ctx);

            Assert.True(result.Success, result.Error);
            var expectedFork = sourceJsonl.Replace("source-sess", "fork-dest", StringComparison.Ordinal);
            Assert.Equal(expectedFork, await harness.WithStoreAsync(store => SessionSnapshotTestSupport.ReadAllTextAsync(store, destKey, ct)));
            var restoredPath = ClaudeCodeAdapter.GetSessionFilePath(
                worktreeDir, "fork-dest", AgentIsolation.AgentUser, AgentIsolation.AgentHome, environment)!;
            Assert.Equal(expectedFork, File.ReadAllText(restoredPath));

            Assert.Equal(sourceJsonl, await harness.WithStoreAsync(store => SessionSnapshotTestSupport.ReadAllTextAsync(store, sourceKey, ct)));
            Assert.Equal(sourceBefore, await harness.WithStoreAsync(store => store.GetMetadataAsync(sourceKey, ct)));
            Assert.Equal(sourceChunksBefore, await harness.ChunkDataAsync(sourceBefore.Id));
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

        var sourceJsonl =
            "{\"type\":\"user\",\"sessionId\":\"source-sess\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}\n" +
            "{\"type\":\"cost-state\",\"sessionId\":\"source-sess\",\"totalCostUSD\":1.00,\"totalAPIDuration\":11774,\"modelUsage\":{}}\n";

        var environment = new TestProcessEnvironment { { "HOME", home } };
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.WithStoreAsync(store => SessionSnapshotTestSupport.SeedAsync(
                store, SessionSnapshotKey.ForRun(runId, "ClaudeCode", "source-sess"), "claude-jsonl", sourceJsonl, TestContext.Current.CancellationToken));

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

        public async Task<T> WithStoreAsync<T>(Func<IAdapterSessionSnapshotStore, Task<T>> use)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await use(scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>());
        }

        public async Task WithStoreAsync(Func<IAdapterSessionSnapshotStore, Task> use)
        {
            await using var scope = _provider.CreateAsyncScope();
            await use(scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>());
        }

        public async Task<List<byte[]>> ChunkDataAsync(Guid snapshotId)
        {
            await using var db = CreateDbContext();
            return await db.AdapterSessionSnapshotChunks
                .Where(c => c.SnapshotId == snapshotId)
                .OrderBy(c => c.Seq)
                .Select(c => c.Data)
                .ToListAsync();
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
