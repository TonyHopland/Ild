using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class WorkItemsController : ControllerBase
{
    private readonly IWorkItemManager _workItemManager;
    private readonly ILoopEngine _engine;
    private readonly AppDbContext _db;
    private readonly ILogger<WorkItemsController> _logger;
    private readonly IWorkItemNotifier _notifier;

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, AppDbContext db, ILogger<WorkItemsController> logger, IWorkItemNotifier? notifier = null)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _db = db;
        _logger = logger;
        _notifier = notifier ?? new NoopWorkItemNotifier();
    }

    private void RunInBackground(Guid runId)
    {
        if (_engine is not ILD.Core.Services.Implementations.LoopEngine le) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await le.RunAsync(runId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background LoopRun {RunId} failed", runId);
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? status = null, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        IQueryable<WorkItem> q = _db.WorkItems.AsNoTracking();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<WorkItemStatus>(status, true, out var s))
            q = q.Where(w => w.Status == s);
        var items = await q.OrderByDescending(w => w.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return Ok(items);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var workItem = await _workItemManager.GetWorkItemAsync(guid);
        if (workItem == null)
            return NotFound();

        return Ok(workItem);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] WorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var loopTemplateId = Guid.TryParse(request.LoopTemplateId, out var ltGuid) ? (Guid?)ltGuid : null;
        var repositoryId = Guid.TryParse(request.RepositoryId, out var rGuid) ? (Guid?)rGuid : null;
        var id = await _workItemManager.CreateWorkItemAsync(
            request.Title, request.Description,
            loopTemplateId, repositoryId);

        var wi = await _workItemManager.GetWorkItemAsync(id);
        if (wi != null)
            await _notifier.WorkItemStateChangedAsync(id, wi.Status, wi.Status);

        return CreatedAtAction(nameof(GetById), new { id }, new { id });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var ok = await _workItemManager.UpdateAsync(guid, request.Title, request.Description);
        if (!ok) return NotFound();
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        return Ok(wi);
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _engine.StartRunAsync(guid);
        return Accepted();
    }

    [HttpPost("{id}/transition")]
    public async Task<IActionResult> Transition(string id, [FromBody] WorkItemTransitionRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (!Enum.TryParse<WorkItemStatus>(request.TargetStatus, true, out var target))
            return BadRequest(new { error = "Invalid target status" });
        var ok = target switch
        {
            WorkItemStatus.WorkQueue => await _workItemManager.TransitionToWorkQueueAsync(guid),
            WorkItemStatus.Ready => await _workItemManager.TransitionToReadyAsync(guid),
            WorkItemStatus.Running => await _workItemManager.TransitionToRunningAsync(guid),
            WorkItemStatus.HumanFeedback => await _workItemManager.TransitionToHumanFeedbackAsync(guid, "manual"),
            WorkItemStatus.Done => await _workItemManager.TransitionToDoneAsync(guid),
            _ => false,
        };
        return ok ? Ok() : BadRequest(new { error = "Transition not allowed" });
    }

    [HttpGet("{id}/dependencies")]
    public async Task<IActionResult> GetDependencies(string id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        var dependencies = await _workItemManager.GetDependenciesAsync(guid);
        return Ok(dependencies.Skip(skip).Take(take));
    }

    [HttpPost("{id}/dependencies")]
    public async Task<IActionResult> AddDependency(string id, [FromBody] AddDependencyRequest request)
    {
        if (!Guid.TryParse(id, out var workItemId) || !Guid.TryParse(request.DependencyId, out var depId))
            return BadRequest(new { error = "Invalid GUID" });

        try
        {
            var success = await _workItemManager.AddDependencyAsync(workItemId, depId);
            return success ? Ok() : BadRequest();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}/dependencies/{depId}")]
    public async Task<IActionResult> RemoveDependency(string id, string depId)
    {
        if (!Guid.TryParse(id, out var wiId) || !Guid.TryParse(depId, out var dId))
            return BadRequest(new { error = "Invalid GUID" });
        var ok = await _workItemManager.RemoveDependencyAsync(wiId, dId);
        return ok ? Ok() : NotFound();
    }

    [HttpGet("{id}/runs")]
    public async Task<IActionResult> GetRuns(string id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var runs = await _db.LoopRuns.AsNoTracking()
            .Where(r => r.WorkItemId == guid)
            .OrderByDescending(r => r.StartedAt)
            .Skip(skip).Take(take)
            .ToListAsync();
        return Ok(runs);
    }

    [HttpPost("{id}/link-pr")]
    public async Task<IActionResult> LinkPr(string id, [FromBody] LinkPrRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.LinkPullRequestAsync(guid, request.PrUrl);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{id}/mark-merged")]
    public async Task<IActionResult> MarkMerged(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var ok = await _workItemManager.ManuallyMarkMergedAsync(guid);
        if (!ok) return NotFound();

        // If there's an active LoopRun, signal the engine to resume via Cleanup
        var activeRun = await _db.LoopRuns
            .FirstOrDefaultAsync(r => r.WorkItemId == guid && r.Status == ILD.Data.Enums.LoopRunStatus.Running);

        if (activeRun != null)
        {
            var prRunNode = await _db.LoopRunNodes
                .FirstOrDefaultAsync(n =>
                    n.LoopRunId == activeRun.Id &&
                    n.LoopNode.NodeType == ILD.Data.Enums.NodeType.PR &&
                    n.Status == ILD.Data.Enums.LoopRunNodeStatus.WaitingHuman);

            if (prRunNode != null)
            {
                await _engine.SignalPrResultAsync(activeRun.Id, prRunNode.Id, true);

                // Resume the engine to route through Cleanup node
                RunInBackground(activeRun.Id);
            }
        }

        return Ok();
    }

    [HttpPost("{id}/human-feedback/input")]
    public async Task<IActionResult> HumanFeedbackInput(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackInputAsync(guid, request.Input);
        if (!ok) return NotFound();

        // Resume the engine
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/human-feedback/reject")]
    public async Task<IActionResult> HumanFeedbackReject(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var ok = await _workItemManager.RejectHumanFeedbackAsync(guid);
        if (!ok) return NotFound();

        // Resume the engine to route failure edge
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/cleanup-to-done")]
    public async Task<IActionResult> CleanupToDone(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var ok = await _workItemManager.CleanupToDoneAsync(guid);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{id}/cleanup-to-backlog")]
    public async Task<IActionResult> CleanupToBacklog(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var ok = await _workItemManager.CleanupToBacklogAsync(guid);
        return ok ? Ok() : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var ok = await _workItemManager.DeleteAsync(guid);
        return ok ? Ok() : NotFound();
    }
}

public class AddDependencyRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string DependencyId { get; set; } = string.Empty;
}

public class HumanFeedbackInputRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(8192, MinimumLength = 1)]
    public string Input { get; set; } = string.Empty;
}
