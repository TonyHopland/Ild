using System.Net.Http.Json;
using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ILD.Tests.Integration;

/// <summary>
/// Two writers appending to one run from different DI scopes, each on its own
/// database connection as on Postgres, with the first one stopped just before
/// its commit. Ids must come out in commit order and a reader paging the events
/// API by cursor must see every event exactly once; two answers to one waiting
/// node must deliver exactly one.
/// </summary>
public sealed class EventLogConcurrentWriterTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _directory;
    private readonly CommitGate _gate = new();
    private readonly ApiFactory _factory;

    public EventLogConcurrentWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ild-eventlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "ild.db");
        using (var schema = SqliteSchemaTemplate<AppDbContext>.OpenCopy(options => new AppDbContext(options)))
        using (var target = new SqliteConnection($"Data Source={file}"))
        {
            target.Open();
            schema.BackupDatabase(target);
            using var wal = target.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        _factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options
                .UseSqlite($"Data Source={file}", sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly))
                .AddInterceptors(_gate));
        });
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Release();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private async Task<Guid> SeedRunAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-" + Guid.NewGuid().ToString("N"),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run.Id;
    }

    /// <summary>An append made from a scope of its own, as a request or a background service would.</summary>
    private Task<long> AppendInOwnScopeAsync(Guid runId, EventType type, string data, string? role = null)
        => Task.Run(async () =>
        {
            if (role is not null) CommitGate.ActAs(role);
            using var scope = _factory.Services.CreateScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            return await events.AppendAsync(runId, type, data);
        });

    private sealed record Page(IReadOnlyList<(long Id, string Payload)> Entries, long NextCursor, bool HasMore);

    private static async Task<Page> ReadPageAsync(HttpClient client, Guid runId, long cursor)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/loopruns/{runId}/events?cursor={cursor}&limit=500", TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("payload").GetString()!))
            .ToList();
        return new Page(entries, root.GetProperty("nextCursor").GetInt64(), root.GetProperty("hasMore").GetBoolean());
    }

    private static async Task<T> Within<T>(Task<T> task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(Patience, TestContext.Current.CancellationToken)) != task)
            Assert.Fail(what);
        return await task;
    }

    private static async Task Within(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(Patience, TestContext.Current.CancellationToken)) != task)
            Assert.Fail(what);
        await task;
    }

    [Fact]
    public async Task A_reader_paging_by_cursor_sees_every_event_once_while_writers_from_other_scopes_commit_in_turn()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync();
        await AppendInOwnScopeAsync(runId, EventType.NodeStarted, "first");
        await AppendInOwnScopeAsync(runId, EventType.NodeStarted, "second");

        var seen = new List<(long Id, string Payload)>();
        var page = await ReadPageAsync(client, runId, 0);
        seen.AddRange(page.Entries);
        var cursor = page.NextCursor;

        var held = AppendInOwnScopeAsync(runId, EventType.NodeStarted, "held", CommitGate.Holder);
        await Within(_gate.Held, "The held writer never reached a commit: an append must commit its row in a transaction.");
        var contender = AppendInOwnScopeAsync(runId, EventType.NodeStarted, "contender", CommitGate.Contender);
        await Within(_gate.ContenderStarted, "The second writer never touched the database.");

        var during = await ReadPageAsync(client, runId, cursor);
        Assert.Empty(during.Entries);
        Assert.Equal(cursor, during.NextCursor);

        _gate.Release();
        var heldId = await Within(held, "The held writer did not finish after its commit was released.");
        var contenderId = await Within(contender, "The second writer did not finish after the first committed.");

        for (var guard = 0; guard < 10; guard++)
        {
            page = await ReadPageAsync(client, runId, cursor);
            seen.AddRange(page.Entries);
            cursor = page.NextCursor;
            if (page.Entries.Count == 0) break;
        }

        Assert.Equal(new[] { "first", "second", "held", "contender" }, seen.Select(e => e.Payload));
        Assert.Equal(seen.Count, seen.Select(e => e.Id).Distinct().Count());
        Assert.True(seen.Select(e => e.Id).SequenceEqual(seen.Select(e => e.Id).Order()), "Ids must increase in commit order.");
        Assert.Equal(heldId, seen[2].Id);
        Assert.Equal(contenderId, seen[3].Id);
    }

    [Fact]
    public async Task A_conversation_event_racing_the_run_ending_event_is_rejected_once_the_end_commits()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync();

        var ending = AppendInOwnScopeAsync(runId, EventType.LoopRunCompleted, "done", CommitGate.Holder);
        await Within(_gate.Held, "The run-ending append never reached a commit: an append must commit its row in a transaction.");
        var reply = AppendInOwnScopeAsync(runId, EventType.HumanFeedbackReceived, "late reply", CommitGate.Contender);
        await Within(_gate.ContenderStarted, "The reply never touched the database.");

        _gate.Release();
        await Within(ending, "The run-ending append did not finish after its commit was released.");
        var rejected = await Within(
            Record.ExceptionAsync(() => reply).AsTask(),
            "The reply neither finished nor failed after the run ended.");

        Assert.IsAssignableFrom<InvalidOperationException>(rejected);
        Assert.Equal("RunClosedException", rejected!.GetType().Name);
        var page = await ReadPageAsync(client, runId, 0);
        Assert.Equal(new[] { "done" }, page.Entries.Select(e => e.Payload));
    }

    private async Task<(string WorkItemId, Guid RunId)> SeedRunWaitingOnAHumanAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example.test" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example.test/repo.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var human = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Human, Label = "Plan check" };
        db.RemoteProviders.Add(remote);
        db.Repositories.Add(repo);
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopNodes.Add(human);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var workItemId = await scope.ServiceProvider.GetRequiredService<IWorkItemManager>()
            .CreateWorkItemAsync("answer me", "", repo.Id);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.WaitingHuman,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = human.Id,
            HumanFeedbackReason = HumanFeedbackReasons.HumanInputNeeded,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.LoopRuns.Add(run);
        db.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = human.Id,
            NodeLabel = human.Label,
            Status = LoopRunNodeStatus.WaitingHuman,
            StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (workItemId, run.Id);
    }

    /// <summary>An answer sent from a request scope of its own, as the API receives one.</summary>
    private Task<IActionResult> AnswerInOwnScopeAsync(string workItemId, Guid runId, string text, string role)
        => Task.Run(async () =>
        {
            CommitGate.ActAs(role);
            using var scope = _factory.Services.CreateScope();
            var controller = ActivatorUtilities.CreateInstance<WorkItemsController>(scope.ServiceProvider);
            return await controller.HumanFeedbackInput(workItemId, new HumanFeedbackInputRequest { RunId = runId, Input = text });
        });

    [Fact]
    public async Task Two_answers_to_the_same_waiting_node_are_delivered_once_and_the_other_is_refused()
    {
        var (workItemId, runId) = await SeedRunWaitingOnAHumanAsync();

        var first = AnswerInOwnScopeAsync(workItemId, runId, "Ship it", CommitGate.Holder);
        await Within(_gate.Held, "The first answer never reached a commit: an answer must be delivered in a transaction.");
        // The first answer has not committed, so the run still reads as waiting
        // to the second: it gets as far as writing before it can find out.
        var second = AnswerInOwnScopeAsync(workItemId, runId, "Hold off", CommitGate.Contender);
        await Within(_gate.ContenderWriting, "The second answer never began to write while the run still read as waiting.");

        _gate.Release();
        var delivered = await Within(first, "The first answer did not finish after its commit was released.");
        var refused = await Within(second, "The second answer neither finished nor failed after the first was delivered.");
        await LoopEngineHarness.WaitUntilIdleAsync((LoopEngine)_factory.Services.GetRequiredService<ILoopEngine>(), runId);

        Assert.IsType<OkResult>(delivered);
        var conflict = Assert.IsAssignableFrom<ObjectResult>(refused);
        Assert.Equal(409, conflict.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var answers = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EventLogs.AsNoTracking()
            .Where(e => e.LoopRunId == runId && e.EventType == EventType.HumanFeedbackReceived)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Ship it", Assert.Single(answers).Data);
    }
}
