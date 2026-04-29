using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IRecoveryManager
{
    Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync();
    Task<bool> RecoverRunAsync(Guid runId);
    Task<bool> ValidateWorktreeHealthAsync(Guid runId);
    Task<RecoveryPolicy> GetRecoveryPolicyAsync(Guid templateId);
    Task SetRecoveryPolicyAsync(Guid templateId, RecoveryPolicy policy);
    Task ClearRecoveryStateAsync(Guid runId);
}
