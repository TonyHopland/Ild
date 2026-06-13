using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class WorkItemsController : ControllerBase
{
    private readonly IWorkItemManager _workItemManager;
    private readonly ILoopEngine _engine;
    private readonly IWorktreePreviewService _worktreePreviewService;
    private readonly ILoopRunStore _loopRunStore;
    private readonly ILogger<WorkItemsController> _logger;
    private readonly IWorkItemNotifier _notifier;
    private readonly IRemoteProvider? _remoteProvider;

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, IWorktreePreviewService worktreePreviewService, ILoopRunStore loopRunStore, ILogger<WorkItemsController> logger, IWorkItemNotifier? notifier = null, IRemoteProvider? remoteProvider = null)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _worktreePreviewService = worktreePreviewService;
        _loopRunStore = loopRunStore;
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
        _ = Task.Run(async () =>
        {
            try
            {
                await _engine.ResumeRecoveredRunAsync(runId);
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
            await _notifier.PreviewStateChangedAsync(id);
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
            var response = await _worktreePreviewService.StopAsync(workItem!.WorktreePath!);
            await _notifier.PreviewStateChangedAsync(id);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/push-branch")]
    public async Task<IActionResult> PushBranch(string id)
    {
        var (_, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        var result = await _workItemManager.CommitAndPushBranchAsync(id);
        if (!result.Success)
            return BadRequest(new { error = result.Error });
        return Ok(new { branch = result.Branch });
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
        var runs = await _loopRunStore.GetByWorkItemPagedAsync(id, skip, take);
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
        var ok = await _workItemManager.MarkMergedAndAdvanceAsync(id);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{id}/human-feedback/input")]
    public async Task<IActionResult> HumanFeedbackInput(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackInputAsync(id, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // SubmitHumanFeedbackInputAsync signals the engine which re-launches the
        // run loop. Calling RunInBackground here would race a second runner and
        // produce duplicate LoopRunNode rows / Interrupted in-flight nodes.
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

        // RejectHumanFeedbackAsync signals the engine which re-launches the run
        // loop along the failure edge. See note on HumanFeedbackInput.
        return Ok();
    }

    [HttpPost("{id}/human-feedback/respond")]
    public async Task<IActionResult> HumanFeedbackRespond(string id, [FromBody] HumanFeedbackInputRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ok = await _workItemManager.SubmitHumanFeedbackRespondAsync(id, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // SubmitHumanFeedbackRespondAsync signals the engine which re-launches
        // the run loop along the respond edge. See note on HumanFeedbackInput.
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
            // Surface the outage rather than masquerading as "no comments" — an
            // empty 200 here is indistinguishable from a PR that genuinely has
            // none, which is the degraded-silently behavior GetAll deliberately
            // rejects (it returns 503 too).
            _logger.LogWarning(ex, "Failed to fetch PR comments for work item {WorkItemId}", id);
            return StatusCode(503, new { error = "Failed to fetch PR comments from remote provider", detail = ex.Message });
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
