using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public class RecoveryManager : IRecoveryManager
{
    private readonly AppDbContext _db;
    private readonly IRepositoryManager _repo;
    private readonly ILoopEngine _engine;

    public RecoveryManager(AppDbContext db, IRepositoryManager repo, ILoopEngine engine)
    {
        _db = db;
        _repo = repo;
        _engine = engine;
    }

    public async Task<IEnumerable<Guid>> GetRecoverableRunIdsAsync()
        => await _db.LoopRuns.Where(r => r.Status == LoopRunStatus.Running).Select(r => r.Id).ToListAsync();

    public async Task<bool> RecoverRunAsync(Guid runId)
    {
        var run = await _db.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null || run.Status != LoopRunStatus.Running) return false;

        var version = await _db.LoopTemplateVersions.FirstOrDefaultAsync(v => v.Id == run.LoopTemplateVersionId);
        var policy = ParsePolicy(run.RecoveryPolicy);
        if (policy == RecoveryPolicy.Cancel)
        {
            await _engine.CancelRunAsync(runId);
            return true;
        }
        if (policy == RecoveryPolicy.NeedsReview)
        {
            var wi = await _db.WorkItems.FirstOrDefaultAsync(w => w.Id == run.WorkItemId);
            if (wi != null)
            {
                wi.Status = WorkItemStatus.HumanFeedback;
                wi.HumanFeedbackReason = "Recovery requires review";
                await _db.SaveChangesAsync();
            }
            return true;
        }

        // AutoResume: re-launch
        if (_engine is LoopEngine le)
            _ = Task.Run(() => le.RunAsync(runId, CancellationToken.None));
        return true;
    }

    public async Task<bool> ValidateWorktreeHealthAsync(Guid runId)
    {
        var run = await _db.LoopRuns.FindAsync(runId);
        if (run == null) return false;
        var wi = await _db.WorkItems.FirstOrDefaultAsync(w => w.Id == run.WorkItemId);
        if (wi == null || string.IsNullOrEmpty(wi.WorktreePath)) return false;
        return await _repo.ValidateWorktreeHealthAsync(wi.WorktreePath);
    }

    public async Task<RecoveryPolicy> GetRecoveryPolicyAsync(Guid templateId)
    {
        var template = await _db.LoopTemplates.FindAsync(templateId);
        return ParsePolicy(template?.RecoveryPolicy);
    }

    public async Task SetRecoveryPolicyAsync(Guid templateId, RecoveryPolicy policy)
    {
        var template = await _db.LoopTemplates.FindAsync(templateId);
        if (template == null) return;
        template.RecoveryPolicy = policy.ToString();
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public Task ClearRecoveryStateAsync(Guid runId) => Task.CompletedTask;

    private static RecoveryPolicy ParsePolicy(string? s)
        => Enum.TryParse<RecoveryPolicy>(s, true, out var p) ? p : RecoveryPolicy.AutoResume;
}
