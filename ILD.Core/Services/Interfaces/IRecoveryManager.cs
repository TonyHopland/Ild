namespace ILD.Core.Services.Interfaces;

public interface IRecoveryManager
{
    Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync();
    Task<bool> RecoverRunAsync(Guid runId);
}
