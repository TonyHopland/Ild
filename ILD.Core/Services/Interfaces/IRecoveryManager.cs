using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
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
