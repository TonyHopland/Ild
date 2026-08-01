using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Implementations;

public class RecoveryManager : IRecoveryManager
{
    private readonly IWorkItemManager _workItems;
    private readonly ILoopRunStore _loopRunStore;
    private readonly ILoopTemplateStore _templateStore;
    private readonly IRepositoryManager _repo;
    private readonly ILoopEngine _engine;

    public RecoveryManager(IWorkItemManager workItems, ILoopRunStore loopRunStore, IProviderStore providerStore, ILoopTemplateStore templateStore, IRepositoryManager repo, ILoopEngine engine)
    {
        _workItems = workItems;
        _loopRunStore = loopRunStore;
        _templateStore = templateStore;
        _repo = repo;
        _engine = engine;
    }

    public async Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync()
    {
        // Two shapes, one query: a run left Running by a crash, and a run the
        // shutdown drain parked at WaitingHuman. The latter is invisible to a
        // Running-only read, and nothing else would ever come back for it.
        // Which shapes those are is LoopRun.IsRecoverable's to say.
        var runs = await _loopRunStore.GetActiveRunsAsync();
        return runs.Where(r => r.IsRecoverable).Select(r => r.Id);
    }

    public async Task<bool> RecoverRunAsync(Guid runId)
    {
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null || !run.IsRecoverable) return false;
        // Which of the two recoverable shapes this is decides how it resumes.
        var shutdownHalted = run.IsShutdownHalted;

        var policy = run.RecoveryPolicy;
        if (policy == RecoveryPolicy.Cancel)
        {
            await _engine.CancelRunAsync(runId);
            return true;
        }
        if (policy == RecoveryPolicy.NeedsReview)
        {
            await _workItems.TransitionAsync(
                run.WorkItemId,
                RemoteWorkItemStatus.HumanFeedback,
                reason: "Recovery requires review");
            return true;
        }

        // Don't auto-resume runs waiting for human input. A shutdown-halted run
        // is parked at its AI node, not at a node asking for a person — that
        // park is the engine's own, and resuming it is the whole point.
        if (!shutdownHalted && run.CurrentNodeId.HasValue)
        {
            var runNode = await _loopRunStore.GetRunNodeAsync(runId, run.CurrentNodeId.Value);
            if (runNode?.Status == LoopRunNodeStatus.WaitingHuman)
            {
                var wi = await _workItems.GetWorkItemAsync(run.WorkItemId);
                if (wi != null && wi.Status != RemoteWorkItemStatus.HumanFeedback)
                {
                    await _workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback);
                }
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(run.WorktreePath)
            && !await _repo.ValidateWorktreeHealthAsync(run.WorktreePath))
        {
            await _workItems.TransitionAsync(
                run.WorkItemId,
                RemoteWorkItemStatus.HumanFeedback,
                reason: $"Recovery requires review: worktree is missing or unhealthy at '{run.WorktreePath}'");
            return true;
        }

        // A shutdown-halted run resumes through the halt path, against the agent
        // session it was parked on: ResumeRecoveredRunAsync would re-drive the AI
        // node cold and throw away the session — the very cost this park exists
        // to avoid. A null note is what makes the executor continue that session
        // with "Continue where you left off."
        if (shutdownHalted)
            await _engine.ResumeFromHaltAsync(runId, null);
        else
            await _engine.ResumeRecoveredRunAsync(runId);
        return true;
    }

    public async Task<bool> ValidateWorktreeHealthAsync(Guid runId)
    {
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null) return false;
        var wi = await _workItems.GetWorkItemAsync(run.WorkItemId);
        if (wi == null || string.IsNullOrEmpty(run.WorktreePath)) return false;
        return await _repo.ValidateWorktreeHealthAsync(run.WorktreePath);
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
