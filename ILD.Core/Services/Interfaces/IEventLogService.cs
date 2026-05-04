using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IEventLogService
{
    Task<long> AppendAsync(Guid runId, string eventType, string message, Guid? nodeId = null, string? payloadPath = null);
    Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null);
    Task<EventLogEntry?> GetBySequenceAsync(Guid runId, long sequence);
    Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before);
    Task<string?> GetPayloadPathAsync(long eventLogId);
    Task<EventLogPage> GetByRunIdAfterCursorAsync(Guid runId, int cursor, int limit);
}
