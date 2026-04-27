using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class LoopEngine : ILoopEngine
{
    private readonly ILogger<LoopEngine> _logger;
    private readonly AppDbContext _dbContext;

    public LoopEngine(ILogger<LoopEngine> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task StartRunAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(nameof(StartRunAsync));
    }

    public Task PauseRunAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(PauseRunAsync));
    }

    public Task ResumeRunAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(ResumeRunAsync));
    }

    public Task CancelRunAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(CancelRunAsync));
    }

    public Task<LoopRunStatus> GetRunStatusAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(GetRunStatusAsync));
    }

    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync()
    {
        throw new NotImplementedException(nameof(GetActiveRunIdsAsync));
    }
}
