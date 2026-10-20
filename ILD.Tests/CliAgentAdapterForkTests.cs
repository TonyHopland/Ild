using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// The shared fork primitive every CLI adapter inherits from
/// <see cref="CliAgentAdapterBase"/> — see ADR-0009 (adapter feature parity).
/// </summary>
public class CliAgentAdapterForkTests
{
    [Fact]
    public async Task Forking_a_session_with_no_snapshot_reports_false_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var runId = await SeedRunAsync(db);
        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => db.OnOwnConnection());
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        await using var provider = services.BuildServiceProvider();
        var adapter = new ForkingAdapter(provider.GetRequiredService<IServiceScopeFactory>());

        var forked = await adapter.ForkAsync(runId, "missing-source", "fork-dest", ct);

        Assert.False(forked);
        Assert.Empty(await new AdapterSessionSnapshotStore(db.Fresh()).ListMetadataForRunAsync(runId, ct));
    }

    private static async Task<Guid> SeedRunAsync(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{Guid.NewGuid():N}", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run.Id;
    }

    private sealed class ForkingAdapter(IServiceScopeFactory scopes) : CliAgentAdapterBase(scopes)
    {
        public override string Name => "ClaudeCode";
        public override string[] SupportedProviderTypes => ["claude-code"];
        public override AgentAdapterTraits Traits => throw new NotSupportedException();
        public override Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => throw new NotSupportedException();

        public Task<bool> ForkAsync(Guid runId, string source, string dest, CancellationToken ct)
            => ForkSessionSnapshotAsync(runId, source, dest, ct);
    }
}
