using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IEventLogService
{
    Task<long> AppendAsync(Guid runId, string eventType, string message, Guid? nodeId = null, Guid? runNodeId = null);
    Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null);
    Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before, ISet<Guid> eligibleRunIds);
    Task<EventLogPage> GetByRunIdAfterCursorAsync(Guid runId, int cursor, int limit);
}
