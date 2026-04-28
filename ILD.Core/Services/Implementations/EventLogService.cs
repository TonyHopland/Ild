using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public class EventLogService : IEventLogService
{
    private readonly AppDbContext _db;
    private readonly EventLogOptions _options;
    private static readonly SemaphoreSlim _seqLock = new(1, 1);

    public EventLogService(AppDbContext db, EventLogOptions? options = null)
    {
        _db = db;
        _options = options ?? new EventLogOptions();
    }

    // Test convenience overload
    public EventLogService(AppDbContext db, string payloadDirectory)
        : this(db, new EventLogOptions { PayloadDirectory = payloadDirectory })
    {
    }

    public async Task<long> AppendAsync(Guid runId, string eventType, string message, string? payloadPath = null)
    {
        if (!Enum.TryParse<EventType>(eventType, ignoreCase: true, out var parsed))
            parsed = EventType.Error;

        await _seqLock.WaitAsync();
        try
        {
            var lastSequence = await _db.EventLogs
                .Where(e => e.LoopRunId == runId)
                .OrderByDescending(e => e.Sequence)
                .Select(e => (int?)e.Sequence)
                .FirstOrDefaultAsync() ?? 0;

            var nextSequence = lastSequence + 1;

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
                Timestamp = DateTime.UtcNow,
                PayloadPath = finalPayloadPath,
                Data = data
            };

            _db.EventLogs.Add(entry);
            await _db.SaveChangesAsync();

            return nextSequence;
        }
        finally
        {
            _seqLock.Release();
        }
    }

    public async Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null)
    {
        var q = _db.EventLogs
            .Where(e => e.LoopRunId == runId)
            .OrderBy(e => e.Sequence)
            .AsQueryable();

        if (limit.HasValue)
            q = q.Take(limit.Value);

        var results = await q.ToListAsync();
        return results.Select(e => new EventLogEntry(
            e.LoopRunId,
            e.EventType.ToString(),
            e.Data ?? string.Empty,
            e.PayloadPath));
    }

    public async Task<EventLogEntry?> GetBySequenceAsync(Guid runId, long sequence)
    {
        var entry = await _db.EventLogs
            .FirstOrDefaultAsync(e => e.LoopRunId == runId && e.Sequence == sequence);
        return entry == null ? null : new EventLogEntry(entry.LoopRunId, entry.EventType.ToString(), entry.Data ?? string.Empty, entry.PayloadPath);
    }

    public async Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before)
    {
        var cutoff = before.UtcDateTime;

        var failedRunIds = await _db.LoopRuns
            .Where(r => r.Status == LoopRunStatus.Failed)
            .Select(r => (Guid?)r.Id)
            .ToListAsync();

        var toRemove = await _db.EventLogs
            .Where(e => e.Timestamp < cutoff && (e.LoopRunId == null || !failedRunIds.Contains(e.LoopRunId)))
            .ToListAsync();

        foreach (var entry in toRemove)
        {
            if (!string.IsNullOrEmpty(entry.PayloadPath) && File.Exists(entry.PayloadPath))
            {
                try { File.Delete(entry.PayloadPath); } catch { /* best-effort */ }
            }
        }

        _db.EventLogs.RemoveRange(toRemove);
        await _db.SaveChangesAsync();
        return toRemove.Count;
    }

    public async Task<string?> GetPayloadPathAsync(long eventLogId)
    {
        var match = await _db.EventLogs.FirstOrDefaultAsync(e => e.Sequence == (int)eventLogId);
        return match?.PayloadPath;
    }
}
