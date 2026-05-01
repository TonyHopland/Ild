using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface ILoopEngine
{
    Task StartRunAsync(Guid workItemId, CancellationToken cancellationToken = default);
    Task PauseRunAsync(Guid runId);
    Task ResumeRunAsync(Guid runId);
    Task CancelRunAsync(Guid runId);
    Task<LoopRunStatus?> GetRunStatusAsync(Guid runId);
    Task<IEnumerable<Guid>> GetActiveRunIdsAsync();
    Task SignalPrResultAsync(Guid runId, Guid prRunNodeId, bool merged);
    Task ResumeRecoveredRunAsync(Guid runId);
}
