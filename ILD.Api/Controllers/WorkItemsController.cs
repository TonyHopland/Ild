using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
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
    private readonly IWorktreePreviewService _worktreePreviewService;
    private readonly AppDbContext _db;
    private readonly ILogger<WorkItemsController> _logger;
    private readonly IWorkItemNotifier _notifier;
    private readonly IRemoteProvider? _remoteProvider;

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, IWorktreePreviewService worktreePreviewService, AppDbContext db, ILogger<WorkItemsController> logger, IWorkItemNotifier? notifier = null, IRemoteProvider? remoteProvider = null)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _worktreePreviewService = worktreePreviewService;
        _db = db;
        _logger = logger;
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _remoteProvider = remoteProvider;
    }

    private async Task<(WorkItemView? WorkItem, IActionResult? Error)> GetPreviewableWorkItemAsync(string id)
    {
        var workItem = await _workItemManager.GetWorkItemAsync(id);
        if (workItem == null)
            return (null, NotFound());
        if (string.IsNullOrWhiteSpace(workItem.WorktreePath))
            return (null, BadRequest(new { error = "Work item does not currently have an active worktree." }));

        return (workItem, null);
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

        RemoteWorkItemStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<RemoteWorkItemStatus>(status, true, out var s))
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
        var workItem = await _workItemManager.GetWorkItemAsync(id);
        if (workItem == null)
            return NotFound();

        return Ok(workItem);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] WorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // RepositoryId is a required FK on the WorkItem entity. Surface a
        // clean 400 instead of letting EF translate this into an opaque
        // "FOREIGN KEY constraint failed" database error.
        if (!Guid.TryParse(request.RepositoryId, out var rGuid))
            return BadRequest(new { error = "repositoryId is required." });
        var repositoryId = (Guid?)rGuid;
        var id = await _workItemManager.CreateWorkItemAsync(
            request.Title, request.Description,
            repositoryId,
            createdByLoopRunId: null,
            forceBacklog: false,
            tags: request.Tags);

        var wi = await _workItemManager.GetWorkItemAsync(id);
        if (wi != null)
            await _notifier.WorkItemStateChangedAsync(id, wi.Status, wi.Status);

        return CreatedAtAction(nameof(GetById), new { id }, wi);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        var ok = await _workItemManager.UpdateAsync(id, request.Title, request.Description, request.Tags);
        if (!ok) return NotFound();
        var wi = await _workItemManager.GetWorkItemAsync(id);
        return Ok(wi);
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        try
        {
            await _engine.StartRunAsync(id);
            return Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("{id}/preview")]
    public async Task<IActionResult> GetPreview(string id)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            return Ok(await _worktreePreviewService.GetStatusAsync(workItem!.WorktreePath!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/preview/start")]
    public async Task<IActionResult> StartPreview(string id, [FromBody] WorktreePreviewStartRequest? request)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _worktreePreviewService.StartAsync(
                workItem!.WorktreePath!,
                new WorktreePreviewStartOptions(
                    request?.ProfileName,
                    request?.SkipInstall == true,
                    request?.PublicHost,
                    request?.PortOverrides));
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/preview/stop")]
    public async Task<IActionResult> StopPreview(string id)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            return Ok(await _worktreePreviewService.StopAsync(workItem!.WorktreePath!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/transition")]
    public async Task<IActionResult> Transition(string id, [FromBody] WorkItemTransitionRequest request)
    {
        if (!Enum.TryParse<WorkItemStatus>(request.TargetStatus, true, out var target))
            return BadRequest(new { error = "Invalid target status" });
        var ok = target switch
        {
            WorkItemStatus.WorkQueue => await _workItemManager.TransitionToWorkQueueAsync(id),
            WorkItemStatus.Ready => await _workItemManager.TransitionToReadyAsync(id),
            WorkItemStatus.Running => await _workItemManager.TransitionToRunningAsync(id),
            WorkItemStatus.HumanFeedback => await _workItemManager.TransitionToHumanFeedbackAsync(id, "manual"),
            WorkItemStatus.Done => await _workItemManager.TransitionToDoneAsync(id),
            _ => false,
        };
        return ok ? Ok() : BadRequest(new { error = "Transition not allowed" });
    }

    [HttpGet("{id}/dependencies")]
    public async Task<IActionResult> GetDependencies(string id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        var dependencies = await _workItemManager.GetDependenciesAsync(id);
        return Ok(dependencies.Skip(skip).Take(take));
    }

    [HttpPost("{id}/dependencies")]
    public async Task<IActionResult> AddDependency(string id, [FromBody] AddDependencyRequest request)
    {
        try
        {
            var success = await _workItemManager.AddDependencyAsync(id, request.DependencyId);
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
        var ok = await _workItemManager.RemoveDependencyAsync(id, depId);
        return ok ? Ok() : NotFound();
    }

    [HttpGet("{id}/runs")]
    public async Task<IActionResult> GetRuns(string id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var runs = await _db.LoopRuns.AsNoTracking()
            .Where(r => r.WorkItemId == id)
            .OrderByDescending(r => r.StartedAt)
            .Skip(skip).Take(take)
            .ToListAsync();
        return Ok(runs);
    }

    [HttpPost("{id}/link-pr")]
    public async Task<IActionResult> LinkPr(string id, [FromBody] LinkPrRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.LinkPullRequestAsync(id, request.PrUrl);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{id}/mark-merged")]
    public async Task<IActionResult> MarkMerged(string id)
    {
        var ok = await _workItemManager.ManuallyMarkMergedAsync(id);
        if (!ok) return NotFound();

        // Only resume the workitem's current run, not stale ones.
        var currentRun = await _db.LoopRuns
            .FirstOrDefaultAsync(r =>
                r.WorkItemId == id &&
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
                        // Unrecoverable — transition work item to Done via manager
                            await _workItemManager.TransitionAsync(id, RemoteWorkItemStatus.Done);
                    }
                    else
                    {
                        await _engine.SignalNodeResultAsync(currentRun.Id, prRunNode.Id, NodeSignal.Succeeded());

                        // SignalNodeResultAsync re-enters the run loop itself;
                        // calling RunInBackground here would race a second runner.
                    }
                }
            }

        return Ok();
    }

    [HttpPost("{id}/human-feedback/input")]
    public async Task<IActionResult> HumanFeedbackInput(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackInputAsync(id, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // Resume the engine
        var wi = await _workItemManager.GetWorkItemAsync(id);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/human-feedback/reject")]
    public async Task<IActionResult> HumanFeedbackReject(string id, [FromBody] HumanFeedbackRejectRequest? request = null)
    {
        // Validate length only when text is supplied; reject without text is valid.
        if (request?.Input is { Length: > 8192 })
            return BadRequest(new { error = "Input exceeds 8192 characters" });

        var ok = await _workItemManager.RejectHumanFeedbackAsync(id, request?.Input);
        if (!ok) return NotFound();

        // Resume the engine to route failure edge
        var wi = await _workItemManager.GetWorkItemAsync(id);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpPost("{id}/human-feedback/respond")]
    public async Task<IActionResult> HumanFeedbackRespond(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackRespondAsync(id, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // Resume the engine to route respond edge
        var wi = await _workItemManager.GetWorkItemAsync(id);
        if (wi?.CurrentLoopRunId != null)
            RunInBackground(wi.CurrentLoopRunId.Value);

        return Ok();
    }

    [HttpGet("{id}/pr-comments")]
    public async Task<IActionResult> GetPrComments(string id)
    {
        var wi = await _workItemManager.GetWorkItemAsync(id);
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
            _logger.LogWarning(ex, "Failed to fetch PR comments for work item {WorkItemId}", id);
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
        var ok = await _workItemManager.CleanupToDoneAsync(id);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{id}/cleanup-to-backlog")]
    public async Task<IActionResult> CleanupToBacklog(string id)
    {
        var ok = await _workItemManager.CleanupToBacklogAsync(id);
        return ok ? Ok() : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var dependents = await _workItemManager.GetDependentsAsync(id);
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

        var ok = await _workItemManager.DeleteAsync(id);
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
