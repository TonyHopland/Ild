using System.Data.Common;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Seeding and reading for the run timeline tests: one run's event log, in the
/// order the store hands it out, and the services that write it, built against
/// a <see cref="TestDb"/> the way production wires them.
/// </summary>
internal static class RunTimeline
{
    public static readonly EventType[] Ending =
        { EventType.LoopRunCompleted, EventType.LoopRunFailed, EventType.LoopRunCancelled };

    /// <summary>The stores of <paramref name="db"/> in a container, for building a service the way DI does.</summary>
    public static ServiceProvider Services(TestDb db, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db.Context);
        services.AddSingleton(db.LoopRuns);
        services.AddSingleton(db.EventLogs);
        services.AddSingleton(db.LoopTemplates);
        services.AddSingleton(db.Providers);
        services.AddSingleton(db.Settings);
        services.AddSingleton(db.ServerClient);
        services.AddSingleton(db.ServerOptions);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static IEventLogService EventLog(TestDb db)
        => ActivatorUtilities.CreateInstance<EventLogService>(Services(db));

    /// <summary>
    /// A remote-backed work item manager on <paramref name="db"/> whose event
    /// writes go through the real event log service.
    /// </summary>
    public static WorkItemManager Manager(TestDb db, ILoopEngine? engine = null, IEventLogService? eventLog = null)
        => new(new Mock<IRepositoryManager>().Object, db.Providers, eventLog ?? EventLog(db), db.LoopRuns,
            db.ServerClient, db.ServerOptions, engine: engine);

    /// <summary>Every event of the run, oldest first.</summary>
    public static IReadOnlyList<EventLog> Events(TestDb db, Guid runId)
        => db.Fresh().EventLogs.AsNoTracking()
            .Where(e => e.LoopRunId == runId)
            .OrderBy(e => e.Id)
            .ToList();

    public static IReadOnlyList<EventLog> Events(TestDb db, Guid runId, EventType type)
        => Events(db, runId).Where(e => e.EventType == type).ToList();

    public static IReadOnlyList<EventLog> EndingEvents(TestDb db, Guid runId)
        => Events(db, runId).Where(e => Ending.Contains(e.EventType)).ToList();

    public static Guid SeedVersion(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t-" + Guid.NewGuid().ToString("N")[..6] };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.SaveChanges();
        return version.Id;
    }

    public static LoopNode SeedNode(TestDb db, Guid versionId, NodeType type, string label)
    {
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = versionId, NodeType = type, Label = label };
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();
        return node;
    }

    public static LoopRun SeedRun(
        TestDb db, string workItemId, Guid versionId, LoopRunStatus status,
        Guid? currentNodeId = null, string? humanFeedbackReason = null, string? worktreePath = null,
        RecoveryPolicy recoveryPolicy = RecoveryPolicy.AutoResume, DateTime? startedAt = null)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            Status = status,
            StartedAt = startedAt ?? DateTime.UtcNow,
            CompletedAt = status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman ? null : DateTime.UtcNow,
            CurrentNodeId = currentNodeId,
            HumanFeedbackReason = humanFeedbackReason,
            WorktreePath = worktreePath,
            RecoveryPolicy = recoveryPolicy,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    public static LoopRunNode SeedRunNode(TestDb db, Guid runId, LoopNode node, LoopRunNodeStatus status, string? label = null)
    {
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = node.Id,
            NodeLabel = label ?? node.Label,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRunNodes.Add(runNode);
        db.Context.SaveChanges();
        return runNode;
    }
}

/// <summary>
/// Scripts an interleaving of two event-log writers on separate connections.
/// The writer acting as <see cref="Holder"/> is stopped just before its first
/// transaction commits — its row inserted, its locks held — until
/// <see cref="Release"/>; the writer acting as <see cref="Contender"/> reports
/// when it has started using the database (its first transaction begun, or its
/// first command finished), so a test knows it is in flight against the held
/// commit rather than merely scheduled.
/// </summary>
internal sealed class CommitGate : IDbTransactionInterceptor, IDbCommandInterceptor
{
    public const string Holder = "holder";
    public const string Contender = "contender";

    private static readonly AsyncLocal<string?> Role = new();

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _contenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holds;

    /// <summary>Marks the calling flow, and everything it awaits, as one of the two writers.</summary>
    public static void ActAs(string role) => Role.Value = role;

    public Task Held => _held.Task;
    public Task ContenderStarted => _contenderStarted.Task;
    public void Release() => _released.TrySetResult();

    private async Task HoldAsync()
    {
        if (Role.Value != Holder || Interlocked.Exchange(ref _holds, 1) == 1) return;
        _held.TrySetResult();
        await _released.Task;
    }

    private void Touched()
    {
        if (Role.Value == Contender) _contenderStarted.TrySetResult();
    }

    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        HoldAsync().GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await HoldAsync();
        return result;
    }

    public InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Touched();
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        Touched();
        return ValueTask.FromResult(result);
    }

    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Touched();
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Touched();
        return ValueTask.FromResult(result);
    }

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Touched();
        return result;
    }

    public ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Touched();
        return ValueTask.FromResult(result);
    }

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Touched();
        return result;
    }

    public ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Touched();
        return ValueTask.FromResult(result);
    }
}
