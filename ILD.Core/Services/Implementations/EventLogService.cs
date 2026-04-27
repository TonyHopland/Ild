using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class EventLogService : IEventLogService
{
    private readonly ILogger<EventLogService> _logger;
    private readonly AppDbContext _dbContext;

    public EventLogService(ILogger<EventLogService> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<long> AppendAsync(Guid runId, string eventType, string message, string? payloadPath = null)
    {
        throw new NotImplementedException(nameof(AppendAsync));
    }

    public Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null)
    {
        throw new NotImplementedException(nameof(GetByRunIdAsync));
    }

    public Task<EventLogEntry?> GetBySequenceAsync(Guid runId, long sequence)
    {
        throw new NotImplementedException(nameof(GetBySequenceAsync));
    }

    public Task<int> EnforceRetentionPolicyAsync(DateTimeOffset before)
    {
        throw new NotImplementedException(nameof(EnforceRetentionPolicyAsync));
    }

    public Task<string?> GetPayloadPathAsync(long eventLogId)
    {
        throw new NotImplementedException(nameof(GetPayloadPathAsync));
    }
}
