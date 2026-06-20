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
/// Fork coverage for the claude-code adapter. Mutates <c>HOME</c> (claude stores
/// session JSONL under <c>$HOME/.claude/projects</c>) so it joins the
/// non-parallel environment collection.
/// </summary>
[Collection("EnvironmentPath")]
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

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            await using var harness = await CreateSessionHarnessAsync();
            var runId = Guid.NewGuid();
            await harness.SeedRunAsync(runId);
            await harness.SeedSnapshotAsync(runId, "ClaudeCode", "source-sess", sourceJson);

            var adapter = new ClaudeCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());
            var ctx = BuildContext(
                binaryPath: "/bin/true",
                worktreePath: worktreeDir,
                runId: runId,
                sessionId: "fork-dest",
                manageSession: true) with { ForkFromSessionId = "source-sess" };

            await adapter.ExecuteAsync(ctx);

            await using var verifyDb = harness.CreateDbContext();
            // Source session is byte-for-byte unchanged after the fork.
            var source = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "ClaudeCode" && s.SessionId == "source-sess");
            Assert.NotNull(source);
            Assert.Equal(sourceJson, source!.SessionJson);
            // A copy now exists under the fork's id, retargeted to that id.
            var fork = await verifyDb.AdapterSessionSnapshots.FirstOrDefaultAsync(s => s.LoopRunId == runId && s.AdapterName == "ClaudeCode" && s.SessionId == "fork-dest");
            Assert.NotNull(fork);
            Assert.Contains("fork-dest", fork!.SessionJson);
            Assert.DoesNotContain("source-sess", fork.SessionJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
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
