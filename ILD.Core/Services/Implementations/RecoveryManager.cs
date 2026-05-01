using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class RecoveryManager : IRecoveryManager
{
    private readonly IWorkItemStore _workItemStore;
    private readonly ILoopRunStore _loopRunStore;
    private readonly ILoopTemplateStore _templateStore;
    private readonly IRepositoryManager _repo;
    private readonly ILoopEngine _engine;

    public RecoveryManager(IWorkItemStore workItemStore, ILoopRunStore loopRunStore, IProviderStore providerStore, ILoopTemplateStore templateStore, IRepositoryManager repo, ILoopEngine engine)
    {
        _workItemStore = workItemStore;
        _loopRunStore = loopRunStore;
        _templateStore = templateStore;
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

        var policy = run.RecoveryPolicy;
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

        await _engine.ResumeRecoveredRunAsync(runId);
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
        var template = await _templateStore.GetByIdAsync(templateId);
        return template?.RecoveryPolicy ?? RecoveryPolicy.AutoResume;
    }

    public async Task SetRecoveryPolicyAsync(Guid templateId, RecoveryPolicy policy)
    {
        var template = await _templateStore.GetByIdAsync(templateId);
        if (template == null) return;
        template.RecoveryPolicy = policy;
        template.UpdatedAt = DateTime.UtcNow;
        await _templateStore.UpdateTemplateAsync(template);
    }

    public Task ClearRecoveryStateAsync(Guid runId) => Task.CompletedTask;
}
