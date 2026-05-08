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
    private readonly IRemoteProvider? _remoteProvider;

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, AppDbContext db, ILogger<WorkItemsController> logger, IWorkItemNotifier? notifier = null, IRemoteProvider? remoteProvider = null)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _db = db;
        _logger = logger;
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _remoteProvider = remoteProvider;
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
    public async Task<IActionResult> GetAll([FromQuery] string? status = null, [FromQuery] string? createdByLoopRunId = null, [FromQuery] string? repositoryId = null, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        WorkItemStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<WorkItemStatus>(status, true, out var s))
            statusFilter = s;
        Guid? runFilter = null;
        if (!string.IsNullOrEmpty(createdByLoopRunId) && Guid.TryParse(createdByLoopRunId, out var runGuid))
            runFilter = runGuid;
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;

        try
        {
            var items = await _workItemManager.ListAsync(statusFilter, runFilter, repoFilter, skip, take);
            return Ok(items);
        }
        catch (InvalidOperationException ex)
        {
            // No remote provider configured.
            _logger.LogWarning(ex, "ListAsync rejected: {Message}", ex.Message);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            // Remote unreachable. Hard cut: do not silently fall back to
            // the local cache; the UI must reflect the outage.
            _logger.LogWarning(ex, "WorkItemServer unreachable for ListAsync");
            return StatusCode(503, new { error = "WorkItemServer unreachable", detail = ex.Message });
        }
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

        return CreatedAtAction(nameof(GetById), new { id }, wi);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var loopTemplateId = Guid.TryParse(request.LoopTemplateId, out var ltGuid) ? (Guid?)ltGuid : null;
        var ok = await _workItemManager.UpdateAsync(guid, request.Title, request.Description, loopTemplateId);
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

        // Only resume the workitem's current run, not stale ones.
        var wi = await _db.WorkItems.FindAsync(guid);
        if (wi?.CurrentLoopRunId != null)
        {
            var currentRun = await _db.LoopRuns
                .FirstOrDefaultAsync(r =>
                    r.Id == wi.CurrentLoopRunId &&
                    r.WorkItemId == guid &&
                    (r.Status == ILD.Data.Enums.LoopRunStatus.Running ||
                     r.Status == ILD.Data.Enums.LoopRunStatus.Failed));

            if (currentRun != null)
            {
                // Try to find the PR node first, then fall back to any waiting/failed node
                // (the LoopNode may not exist if the template was updated since the run started)
                var prRunNode = await _db.LoopRunNodes
                    .Include(n => n.LoopNode)
                    .FirstOrDefaultAsync(n =>
                        n.LoopRunId == currentRun.Id &&
                        n.LoopNode.NodeType == ILD.Data.Enums.NodeType.PR &&
                        (n.Status == ILD.Data.Enums.LoopRunNodeStatus.WaitingHuman ||
                         n.Status == ILD.Data.Enums.LoopRunNodeStatus.Failed));

                if (prRunNode == null)
                {
                    prRunNode = await _db.LoopRunNodes
                        .Include(n => n.LoopNode)
                        .FirstOrDefaultAsync(n =>
                            n.LoopRunId == currentRun.Id &&
                            (n.Status == ILD.Data.Enums.LoopRunNodeStatus.WaitingHuman ||
                             n.Status == ILD.Data.Enums.LoopRunNodeStatus.Failed));
                }

                if (prRunNode != null)
                {
                    // If the node's LoopNode is gone (template was updated), the run is
                    // unrecoverable — the engine can't route to the Cleanup node. Complete
                    // the run and mark the workitem Done directly.
                    if (prRunNode.LoopNode == null)
                    {
                        currentRun.Status = ILD.Data.Enums.LoopRunStatus.Completed;
                        currentRun.CompletedAt = DateTime.UtcNow;
                        wi.Status = WorkItemStatus.Done;
                        wi.HumanFeedbackReason = null;
                        await _db.SaveChangesAsync();
                    }
                    else
                    {
                        await _engine.SignalNodeResultAsync(currentRun.Id, prRunNode.Id, NodeSignal.Succeeded());

                        // SignalNodeResultAsync re-enters the run loop itself;
                        // calling RunInBackground here would race a second runner.
                    }
                }
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

        var ok = await _workItemManager.SubmitHumanFeedbackInputAsync(guid, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // Resume the engine
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/human-feedback/reject")]
    public async Task<IActionResult> HumanFeedbackReject(string id, [FromBody] HumanFeedbackRejectRequest? request = null)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        // Validate length only when text is supplied; reject without text is valid.
        if (request?.Input is { Length: > 8192 })
            return BadRequest(new { error = "Input exceeds 8192 characters" });

        var ok = await _workItemManager.RejectHumanFeedbackAsync(guid, request?.Input);
        if (!ok) return NotFound();

        // Resume the engine to route failure edge
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/human-feedback/respond")]
    public async Task<IActionResult> HumanFeedbackRespond(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackRespondAsync(guid, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // Resume the engine to route respond edge
        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpGet("{id}/pr-comments")]
    public async Task<IActionResult> GetPrComments(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var wi = await _workItemManager.GetWorkItemAsync(guid);
        if (wi == null) return NotFound();
        if (string.IsNullOrEmpty(wi.PrUrl)) return Ok(Array.Empty<RemotePrComment>());
        if (_remoteProvider == null) return Ok(Array.Empty<RemotePrComment>());

        var prNumber = ExtractPrNumber(wi.PrUrl);
        if (prNumber == null) return Ok(Array.Empty<RemotePrComment>());

        var repoUrl = wi.PrUrl[..wi.PrUrl.IndexOf("/pulls/", StringComparison.Ordinal)];

        try
        {
            var comments = await _remoteProvider.GetPullRequestCommentsAsync(repoUrl, prNumber);
            return Ok(comments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch PR comments for work item {WorkItemId}", guid);
            return Ok(Array.Empty<RemotePrComment>());
        }
    }

    private static string? ExtractPrNumber(string prUrl)
    {
        var marker = "/pulls/";
        var idx = prUrl.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = prUrl[(idx + marker.Length)..].Trim('/');
        if (tail.Length == 0) return null;
        // Strip any trailing path or query.
        var slash = tail.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0) tail = tail[..slash];
        return tail;
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

        var dependents = await _workItemManager.GetDependentsAsync(guid);
        var dependentList = dependents.ToList();
        if (dependentList.Count > 0)
        {
            var titles = dependentList.Select(d => d.Title).ToList();
            return Conflict(new
            {
                error = "Cannot delete work item that has dependents",
                dependents = titles,
            });
        }

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
    /// <summary>
    /// Optional human acknowledgement / additional context. Empty input is
    /// allowed: the human may simply approve the suspended node. When supplied
    /// the text becomes <c>{{PreviousNode.Output}}</c> for the OnSuccess
    /// successor.
    /// </summary>
    [System.ComponentModel.DataAnnotations.StringLength(8192)]
    public string? Input { get; set; }
}

public class HumanFeedbackRejectRequest
{
    /// <summary>
    /// Optional rejection rationale. When supplied it is stored on the
    /// suspended run node's <c>Output</c> so the OnFailure successor can
    /// read it via <c>{{PreviousNode.Output}}</c>.
    /// </summary>
    public string? Input { get; set; }
}
