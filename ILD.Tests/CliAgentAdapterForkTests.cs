using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// The shared fork and session-file save primitives every CLI adapter inherits from
/// <see cref="CliAgentAdapterBase"/> — see ADR-0009 (adapter feature parity).
/// </summary>
public class CliAgentAdapterForkTests
{
    [Fact]
    public async Task Forking_a_session_with_no_snapshot_reports_false_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var runId = await TestRuns.SeedAsync(db);
        await using var provider = SnapshotServices(db);
        var adapter = new SnapshotAdapter(provider.GetRequiredService<IServiceScopeFactory>());

        var forked = await adapter.ForkAsync(runId, "missing-source", "fork-dest", ct);

        Assert.False(forked);
        Assert.Empty(await new AdapterSessionSnapshotStore(db.Fresh()).ListMetadataForRunAsync(runId, ct));
    }

    [Fact]
    public async Task An_empty_session_file_leaves_the_stored_history_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var runId = await TestRuns.SeedAsync(db);
        var key = SessionSnapshotKey.ForRun(runId, "ClaudeCode", "sess-1");
        await SessionSnapshotTestSupport.SeedAsync(new AdapterSessionSnapshotStore(db.Fresh()), key, SessionSnapshotFormats.ClaudeJsonl, "{\"turn\":1}\n", ct);
        await using var provider = SnapshotServices(db);
        var dir = Directory.CreateTempSubdirectory("ild-persist-").FullName;
        try
        {
            var path = Path.Combine(dir, "sess-1.jsonl");
            File.WriteAllBytes(path, []);

            await new SnapshotAdapter(provider.GetRequiredService<IServiceScopeFactory>()).PersistAsync(runId, "sess-1", path, NullLogger.Instance, ct);

            Assert.Equal("{\"turn\":1}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(new AdapterSessionSnapshotStore(db.Fresh()), key, ct));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_save_cut_short_by_a_cancelled_turn_is_not_reported_as_a_failure_and_does_not_throw()
    {
        using var db = new TestDb();
        var runId = await TestRuns.SeedAsync(db);
        await using var provider = SnapshotServices(db);
        var dir = Directory.CreateTempSubdirectory("ild-persist-").FullName;
        var log = new ListLogger<CliAgentAdapterForkTests>();
        try
        {
            var path = Path.Combine(dir, "sess-1.jsonl");
            File.WriteAllText(path, "{\"turn\":1}\n");

            await new SnapshotAdapter(provider.GetRequiredService<IServiceScopeFactory>()).PersistAsync(runId, "sess-1", path, log, new CancellationToken(canceled: true));

            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            Assert.Contains(log.Entries, e => e.Message.Contains("cancelled", StringComparison.Ordinal));
            Assert.Empty(await new AdapterSessionSnapshotStore(db.Fresh()).ListMetadataForRunAsync(runId, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ServiceProvider SnapshotServices(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => db.OnOwnConnection());
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        return services.BuildServiceProvider();
    }

    private sealed class SnapshotAdapter(IServiceScopeFactory scopes) : CliAgentAdapterBase(scopes)
    {
        public override string Name => "ClaudeCode";
        public override string[] SupportedProviderTypes => ["claude-code"];
        public override AgentAdapterTraits Traits => throw new NotSupportedException();
        public override Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => throw new NotSupportedException();

        public Task<bool> ForkAsync(Guid runId, string source, string dest, CancellationToken ct)
            => ForkSessionSnapshotAsync(runId, source, dest, ct);

        public Task PersistAsync(Guid runId, string sessionId, string path, ILogger logger, CancellationToken ct)
            => PersistSessionFileAsync(
                new AgentExecutionContext(
                    Provider: new AiProvider { Name = "p", Type = "claude-code", BaseUrl = string.Empty, Model = string.Empty },
                    Prompt: "prompt",
                    RunContext: new LoopRunContext(runId, "wi", "t", "d", Path.GetTempPath(), "main", new List<string>(), null),
                    ExecutionCount: 1,
                    Cancel: ct,
                    SessionId: sessionId,
                    ManageSession: true),
                sessionId, path, SessionSnapshotFormats.ClaudeJsonl, logger);
    }
}
