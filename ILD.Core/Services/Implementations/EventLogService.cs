using System.Collections.Concurrent;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public class EventLogService : IEventLogService
{
    private readonly IEventLogStore _eventLogStore;
    private readonly ILoopRunStore _loopRunStore;
    private readonly EventLogOptions _options;
    private readonly ILogger<EventLogService>? _logger;

    // Per-run lock guards the sequence allocate -> file-write -> insert path so
    // concurrent appends within a run cannot interleave payload-file writes
    // ahead of their event row. Cross-run appends never block each other.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();

    public EventLogService(IEventLogStore eventLogStore, ILoopRunStore loopRunStore, EventLogOptions? options = null, ILogger<EventLogService>? logger = null)
    {
        _eventLogStore = eventLogStore;
        _loopRunStore = loopRunStore;
        _options = options ?? new EventLogOptions();
        _logger = logger;
    }

    public EventLogService(IEventLogStore eventLogStore, ILoopRunStore loopRunStore, string payloadDirectory)
        : this(eventLogStore, loopRunStore, new EventLogOptions { PayloadDirectory = payloadDirectory })
    {
    }

    public async Task<long> AppendAsync(Guid runId, string eventType, string message, Guid? nodeId = null, string? payloadPath = null, Guid? runNodeId = null)
    {
        if (!Enum.TryParse<EventType>(eventType, ignoreCase: true, out var parsed))
        {
            // An unrecognized event-type string is a programming error (typo / drift from
            // the EventType enum), not a runtime "Error" event. Coercing it silently to
            // EventType.Error mislabels successful events as failures, so log it loudly.
            _logger?.LogError(
                "Unknown event type '{EventType}' for run {RunId} (node {NodeId}); recording as Error. " +
                "This string does not match any EventType enum member.",
                eventType, runId, nodeId);
            parsed = EventType.Error;
        }

        var runLock = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await runLock.WaitAsync();
        try
        {
            var nextSequence = await _loopRunStore.AllocateNextEventSequenceAsync(runId);

            string? finalPayloadPath = payloadPath;
            string? data = message;

            if (message != null && System.Text.Encoding.UTF8.GetByteCount(message) > _options.LargePayloadThresholdBytes)
            {
                Directory.CreateDirectory(Path.Combine(_options.PayloadDirectory, runId.ToString()));
                var path = Path.Combine(_options.PayloadDirectory, runId.ToString(), $"{nextSequence}.json");
                await File.WriteAllTextAsync(path, message);
                finalPayloadPath = path;
                data = null;
            }

            var entry = new EventLog
            {
                Id = Guid.NewGuid(),
                LoopRunId = runId,
                Sequence = nextSequence,
                EventType = parsed,
                NodeId = nodeId,
                RunNodeId = runNodeId,
                Timestamp = DateTime.UtcNow,
                PayloadPath = finalPayloadPath,
                Data = data
            };

            await _eventLogStore.AppendAsync(entry);

            return nextSequence;
        }
        finally
        {
            runLock.Release();
        }
    }

    public async Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null)
    {
        var entries = await _eventLogStore.GetByRunIdAsync(runId);
        var list = entries.ToList();

        if (limit.HasValue)
            list = list.Take(limit.Value).ToList();

        return list.Select(e => new EventLogEntry(
            e.LoopRunId,
            e.EventType.ToString(),
            e.Data ?? string.Empty,
            e.PayloadPath,
            e.RunNodeId,
            e.Timestamp));
    }

    public async Task<EventLogEntry?> GetBySequenceAsync(Guid runId, long sequence)
    {
        var entry = await _eventLogStore.GetBySequenceAsync(runId, (int)sequence);
        return entry == null ? null : new EventLogEntry(entry.LoopRunId, entry.EventType.ToString(), entry.Data ?? string.Empty, entry.PayloadPath, entry.RunNodeId, entry.Timestamp);
    }

    public async Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before, ISet<Guid> eligibleRunIds)
    {
        if (eligibleRunIds.Count == 0) return 0;

        var older = await _eventLogStore.GetOlderThanAsync(before);
        var toRemove = older
            .Where(e => e.LoopRunId.HasValue && eligibleRunIds.Contains(e.LoopRunId.Value))
            .ToList();

        foreach (var entry in toRemove)
        {
            if (!string.IsNullOrEmpty(entry.PayloadPath) && File.Exists(entry.PayloadPath))
            {
                try { File.Delete(entry.PayloadPath); } catch { /* best-effort */ }
            }
        }

        await _eventLogStore.RemoveRangeAsync(toRemove);
        return toRemove.Count;
    }

    public async Task<string?> GetPayloadPathAsync(long eventLogId)
    {
        var match = await _eventLogStore.GetByRunIdAsync(Guid.Empty);
        var found = match.FirstOrDefault(e => e.Sequence == (int)eventLogId);
        return found?.PayloadPath;
    }

    public async Task<EventLogPage> GetByRunIdAfterCursorAsync(Guid runId, int cursor, int limit)
    {
        var entries = await _eventLogStore.GetByRunIdAfterCursorAsync(runId, cursor, limit);
        var list = entries.ToList();
        var hasMore = list.Count >= limit;
        var nextCursor = list.Count > 0 ? list[^1].Sequence : cursor;

        return new EventLogPage
        {
            Entries = list,
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }
}
