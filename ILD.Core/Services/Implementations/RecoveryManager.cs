using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class RecoveryManager : IRecoveryManager
{
    private readonly ILogger<RecoveryManager> _logger;
    private readonly AppDbContext _dbContext;

    public RecoveryManager(ILogger<RecoveryManager> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync()
    {
        throw new NotImplementedException(nameof(GetRecoverableRunIdsAsync));
    }

    public Task<bool> RecoverRunAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(RecoverRunAsync));
    }

    public Task<bool> ValidateWorktreeHealthAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(ValidateWorktreeHealthAsync));
    }

    public Task<RecoveryPolicy> GetRecoveryPolicyAsync(Guid templateId)
    {
        throw new NotImplementedException(nameof(GetRecoveryPolicyAsync));
    }

    public Task SetRecoveryPolicyAsync(Guid templateId, RecoveryPolicy policy)
    {
        throw new NotImplementedException(nameof(SetRecoveryPolicyAsync));
    }

    public Task ClearRecoveryStateAsync(Guid runId)
    {
        throw new NotImplementedException(nameof(ClearRecoveryStateAsync));
    }
}
