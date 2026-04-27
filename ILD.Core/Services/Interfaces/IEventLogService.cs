using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Core.Services.Interfaces;

public interface IEventLogService
{
    Task<long> AppendAsync(Guid runId, string eventType, string message, string? payloadPath = null);
    Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null);
    Task<EventLogEntry?> GetBySequenceAsync(Guid runId, long sequence);
    Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before);
    Task<string?> GetPayloadPathAsync(long eventLogId);
}
