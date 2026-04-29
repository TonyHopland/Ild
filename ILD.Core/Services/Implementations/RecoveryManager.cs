using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class RecoveryManager : IRecoveryManager
{
    private readonly IWorkItemStore _workItemStore;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IProviderStore _providerStore;
    private readonly IRepositoryManager _repo;
    private readonly ILoopEngine _engine;

    public RecoveryManager(IWorkItemStore workItemStore, ILoopRunStore loopRunStore, IProviderStore providerStore, IRepositoryManager repo, ILoopEngine engine)
    {
        _workItemStore = workItemStore;
        _loopRunStore = loopRunStore;
        _providerStore = providerStore;
        _repo = repo;
        _engine = engine;
    }

    public async Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync()
    {
        var runs = await _loopRunStore.GetRunningRunsAsync();
        return runs.Select(r => r.Id);
    }

    public async Task<bool> RecoverRunAsync(Guid runId)
    {
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null || run.Status != LoopRunStatus.Running) return false;

        var policy = ParsePolicy(run.RecoveryPolicy);
        if (policy == RecoveryPolicy.Cancel)
        {
            await _engine.CancelRunAsync(runId);
            return true;
        }
        if (policy == RecoveryPolicy.NeedsReview)
        {
            var wi = await _workItemStore.GetByIdAsync(run.WorkItemId);
            if (wi != null)
            {
                wi.Status = WorkItemStatus.HumanFeedback;
                wi.HumanFeedbackReason = "Recovery requires review";
                wi.UpdatedAt = DateTime.UtcNow;
                await _workItemStore.UpdateAsync(wi);
            }
            return true;
        }

        if (_engine is LoopEngine le)
            _ = Task.Run(() => le.RunAsync(runId, CancellationToken.None));
        return true;
    }

    public async Task<bool> ValidateWorktreeHealthAsync(Guid runId)
    {
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null) return false;
        var wi = await _workItemStore.GetByIdAsync(run.WorkItemId);
        if (wi == null || string.IsNullOrEmpty(wi.WorktreePath)) return false;
        return await _repo.ValidateWorktreeHealthAsync(wi.WorktreePath);
    }

    public async Task<RecoveryPolicy> GetRecoveryPolicyAsync(Guid templateId)
    {
        var template = await _providerStore.GetLoopTemplateByIdAsync(templateId);
        return ParsePolicy(template?.RecoveryPolicy);
    }

    public async Task SetRecoveryPolicyAsync(Guid templateId, RecoveryPolicy policy)
    {
        var template = await _providerStore.GetLoopTemplateByIdAsync(templateId);
        if (template == null) return;
        template.RecoveryPolicy = policy.ToString();
        template.UpdatedAt = DateTime.UtcNow;
        await _providerStore.UpdateLoopTemplateAsync(template);
    }

    public Task ClearRecoveryStateAsync(Guid runId) => Task.CompletedTask;

    private static RecoveryPolicy ParsePolicy(string? s)
        => Enum.TryParse<RecoveryPolicy>(s, true, out var p) ? p : RecoveryPolicy.AutoResume;
}
