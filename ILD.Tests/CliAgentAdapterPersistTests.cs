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

/// <summary>The session-file save every CLI adapter inherits from <see cref="CliAgentAdapterBase"/>.</summary>
public sealed class CliAgentAdapterPersistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-persist-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task An_empty_session_file_leaves_the_stored_history_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var runId = await SeedRunAsync(db);
        var key = SessionSnapshotKey.ForRun(runId, "ClaudeCode", "sess-1");
        await SessionSnapshotTestSupport.SeedAsync(new AdapterSessionSnapshotStore(db.Fresh()), key, SessionSnapshotFormats.ClaudeJsonl, "{\"turn\":1}\n", ct);
        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => db.OnOwnConnection());
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        await using var provider = services.BuildServiceProvider();
        var path = Path.Combine(_dir, "sess-1.jsonl");
        File.WriteAllBytes(path, []);

        await new PersistingAdapter(provider.GetRequiredService<IServiceScopeFactory>()).PersistAsync(runId, "sess-1", path);

        Assert.Equal("{\"turn\":1}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(new AdapterSessionSnapshotStore(db.Fresh()), key, ct));
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

    private sealed class PersistingAdapter(IServiceScopeFactory scopes) : CliAgentAdapterBase(scopes)
    {
        public override string Name => "ClaudeCode";
        public override string[] SupportedProviderTypes => ["claude-code"];
        public override AgentAdapterTraits Traits => throw new NotSupportedException();
        public override Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => throw new NotSupportedException();

        public Task PersistAsync(Guid runId, string sessionId, string path)
            => PersistSessionFileAsync(
                new AgentExecutionContext(
                    Provider: new AiProvider { Name = "p", Type = "claude-code", BaseUrl = string.Empty, Model = string.Empty },
                    Prompt: "prompt",
                    RunContext: new LoopRunContext(runId, "wi", "t", "d", Path.GetTempPath(), "main", new List<string>(), null),
                    ExecutionCount: 1,
                    Cancel: TestContext.Current.CancellationToken,
                    SessionId: sessionId,
                    ManageSession: true),
                sessionId, path, SessionSnapshotFormats.ClaudeJsonl,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }
}
