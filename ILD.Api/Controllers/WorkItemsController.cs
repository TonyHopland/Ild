using ILD.Api.Contracts;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores;
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
    private readonly IRepositoryManager _repositoryManager;
    private readonly ILoopRunStore _loopRunStore;
    private readonly ILogger<WorkItemsController> _logger;
    private readonly IWorkItemNotifier _notifier;
    private readonly IRemoteProvider? _remoteProvider;
    private readonly IProviderStore _providerStore;
    private readonly IBranchNameOverrideService _branchNames;

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, IWorktreePreviewService worktreePreviewService, IRepositoryManager repositoryManager, ILoopRunStore loopRunStore, IProviderStore providerStore, IBranchNameOverrideService branchNames, ILogger<WorkItemsController> logger, IWorkItemNotifier? notifier = null, IRemoteProvider? remoteProvider = null)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _worktreePreviewService = worktreePreviewService;
        _repositoryManager = repositoryManager;
        _loopRunStore = loopRunStore;
        _providerStore = providerStore;
        _logger = logger;
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _remoteProvider = remoteProvider;
        _branchNames = branchNames;
    }

    // The branch the worktree diff forks from — resolved here rather than left
    // to the worktree's own origin/HEAD, which isn't always set and would
    // collapse the diff.
    //
    // It has to be the base that worktree was actually built on, or the Files
    // tab reports every commit the base has diverged by as this item's work.
    // The run pinned that base at creation, so read it from the run holding
    // this worktree rather than from the work item, whose override may have
    // been edited since (that edit only reaches the item's next run) — see
    // ADR-0008.
    private async Task<string?> ResolveDiffBaseBranchAsync(WorkItemView workItem)
    {
        if (!string.IsNullOrWhiteSpace(workItem.WorktreePath))
        {
            var run = await _loopRunStore.GetByWorktreePathAsync(workItem.WorktreePath);
            if (!string.IsNullOrWhiteSpace(run?.BaseBranchOverride))
                return run!.BaseBranchOverride;
        }
        if (workItem.RepositoryId is null) return null;
        var repo = await _providerStore.GetRepositoryByIdAsync(workItem.RepositoryId.Value);
        return repo?.DefaultBranch;
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

        // A custom branch name is used verbatim as a branch and a worktree
        // directory, so an illegal one is refused rather than mangled.
        var branchNameOverride = BranchNameRules.Normalize(request.BranchNameOverride);
        if (branchNameOverride is not null && BranchNameRules.Validate(branchNameOverride) is { } branchError)
            return BadRequest(new { error = branchError });

        // The base branch reaches git as a ref too, so the same rules apply.
        // Whether it actually exists is not settled here — it is checked against
        // origin when the run starts, which is the only moment that binds.
        var baseBranchOverride = BranchNameRules.Normalize(request.BaseBranchOverride);
        if (baseBranchOverride is not null
            && BranchNameRules.Validate(baseBranchOverride, BranchNameRules.BaseBranchSubject) is { } baseError)
            return BadRequest(new { error = baseError });

        var id = await _workItemManager.CreateWorkItemAsync(
            request.Title, request.Description,
            repositoryId,
            createdByLoopRunId: null,
            forceBacklog: false,
            tags: request.Tags,
            branchNameOverride: branchNameOverride,
            baseBranchOverride: baseBranchOverride);

        // Creation broadcasts over SignalR from WorkItemManager.CreateWorkItemAsync,
        // so connected clients pick up the new item live without a duplicate here.
        var wi = await _workItemManager.GetWorkItemAsync(id);
        return CreatedAtAction(nameof(GetById), new { id }, wi);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        // A supplied (parseable) override mode replaces the work item's override;
        // the target id travels with it. An unset/unparseable mode leaves the
        // existing override untouched.
        RemoteAiProviderOverrideMode? overrideMode = null;
        Guid? overrideProviderId = null;
        if (!string.IsNullOrWhiteSpace(request.AiProviderOverride)
            && Enum.TryParse<RemoteAiProviderOverrideMode>(request.AiProviderOverride, ignoreCase: true, out var parsedMode))
        {
            overrideMode = parsedMode;
            if (Guid.TryParse(request.AiProviderOverrideId, out var parsedProviderId))
                overrideProviderId = parsedProviderId;
        }

        // Same rule as create, and the same reason it must be enforced here:
        // editing the branch name is how a human clears a conflict, so the edit
        // path is a first-class way to introduce an illegal one. A blank value
        // is a deliberate "go back to generated naming" and passes through.
        if (BranchNameRules.Normalize(request.BranchNameOverride) is { } branchName
            && BranchNameRules.Validate(branchName) is { } branchError)
            return BadRequest(new { error = branchError });

        // Likewise for the base branch: a blank value is a deliberate "go back
        // to the repository default" and passes through untouched.
        if (BranchNameRules.Normalize(request.BaseBranchOverride) is { } baseBranch
            && BranchNameRules.Validate(baseBranch, BranchNameRules.BaseBranchSubject) is { } baseError)
            return BadRequest(new { error = baseError });

        var ok = await _workItemManager.UpdateAsync(
            id, request.Title, request.Description, request.Tags, overrideMode, overrideProviderId,
            request.BranchNameOverride, request.BaseBranchOverride);
        if (!ok) return NotFound();
        var wi = await _workItemManager.GetWorkItemAsync(id);
        return Ok(wi);
    }

    /// <summary>
    /// Advice on a custom branch name while it is being typed: whether it is
    /// legal, and whether anything already holds it. Deliberately advisory —
    /// the world moves between here and the run, so the binding conflict check
    /// is the one the engine takes at run start (ADR-0008). A caller that gets
    /// a warning here is still free to save.
    /// </summary>
    [HttpGet("branch-name-check")]
    public async Task<IActionResult> CheckBranchName(
        [FromQuery] string? name,
        [FromQuery] string? repositoryId,
        [FromQuery] string? workItemId,
        CancellationToken cancellationToken)
    {
        if (BranchNameRules.Normalize(name) is null)
            return Ok(new { error = (string?)null, warning = (string?)null });

        Guid? repoId = Guid.TryParse(repositoryId, out var parsed) ? parsed : null;
        var verdict = await _branchNames.InspectAsync(name, repoId, workItemId, cancellationToken);
        return Ok(new { error = verdict.ValidationError, warning = verdict.Conflict });
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
                    request?.PortOverrides,
                    await _providerStore.GetRepositoryPreviewEnvAsync(workItem!.RepositoryId),
                    workItem!.Id));
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

    [HttpPost("{id}/preview/services/{service}/start")]
    public async Task<IActionResult> StartPreviewService(string id, string service, [FromBody] WorktreePreviewStartRequest? request)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _worktreePreviewService.StartServiceAsync(
                workItem!.WorktreePath!,
                service,
                new WorktreePreviewStartOptions(
                    request?.ProfileName,
                    request?.SkipInstall == true,
                    request?.PublicHost,
                    request?.PortOverrides,
                    await _providerStore.GetRepositoryPreviewEnvAsync(workItem!.RepositoryId),
                    workItem!.Id));
            await _notifier.PreviewStateChangedAsync(id);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/preview/services/{service}/stop")]
    public async Task<IActionResult> StopPreviewService(string id, string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _worktreePreviewService.StopServiceAsync(workItem!.WorktreePath!, service);
            await _notifier.PreviewStateChangedAsync(id);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id}/preview/services/{service}/config")]
    public async Task<IActionResult> GetPreviewServiceConfig(string id, string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var config = await _worktreePreviewService.GetServiceConfigAsync(workItem!.WorktreePath!, service);
            if (config == null)
                return NotFound(new { error = $"No preview config found for service '{service}'." });
            return Ok(new WorktreePreviewServiceConfigResponse { Service = service, Config = config });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}/preview/services/{service}/config")]
    public async Task<IActionResult> UpdatePreviewServiceConfig(string id, string service, [FromBody] WorktreePreviewServiceConfigUpdateRequest? request)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });
        if (request == null || string.IsNullOrWhiteSpace(request.Config))
            return BadRequest(new { error = "config is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            await _worktreePreviewService.UpdateServiceConfigAsync(workItem!.WorktreePath!, service, request.Config);
            var config = await _worktreePreviewService.GetServiceConfigAsync(workItem!.WorktreePath!, service);
            return Ok(new WorktreePreviewServiceConfigResponse { Service = service, Config = config });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id}/preview/logs")]
    public async Task<IActionResult> GetPreviewLog(string id, [FromQuery] string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var content = await _worktreePreviewService.GetServiceLogAsync(workItem!.WorktreePath!, service);
            return Ok(new WorktreePreviewLogResponse { Service = service, Content = content });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id}/files")]
    public async Task<IActionResult> GetFiles(string id)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        var diffBase = await ResolveDiffBaseBranchAsync(workItem!);
        var files = await _repositoryManager.ListWorktreeFilesAsync(workItem!.WorktreePath!, diffBase);
        return Ok(new WorktreeFilesResponse
        {
            WorktreePath = workItem.WorktreePath!,
            Files = files.ToList(),
        });
    }

    [HttpGet("{id}/files/content")]
    public async Task<IActionResult> GetFileContent(string id, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        var diffBase = await ResolveDiffBaseBranchAsync(workItem!);
        var content = await _repositoryManager.ReadWorktreeFileAsync(workItem!.WorktreePath!, path, diffBase);
        if (content == null)
            return NotFound(new { error = "File not found in worktree." });
        return Ok(content);
    }

    // Writes land in the worktree and stop there — git is left alone, so an edit
    // saved here reaches a branch the same way the run's own edits do.
    [HttpPut("{id}/files/content")]
    public async Task<IActionResult> SaveFileContent(string id, [FromBody] WorktreeFileSaveRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "path is required." });
        if (request.Content == null)
            return BadRequest(new { error = "content is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        var diffBase = await ResolveDiffBaseBranchAsync(workItem!);
        var result = await _repositoryManager.WriteWorktreeFileAsync(workItem!.WorktreePath!, request.Path, request.Content, diffBase);
        // A save that produced a file answers with it — asking for the file is
        // the same question as asking whether it saved, so there is no arm here
        // that could answer 200 with nothing in it. A file that is not there
        // answers as it does when read, so the two endpoints do not disagree
        // about the same path.
        if (result.File is { } saved)
            return Ok(saved);
        return result.Outcome switch
        {
            WorktreeFileWriteOutcome.NotFound => NotFound(new { error = "File not found in worktree." }),
            WorktreeFileWriteOutcome.NotText => BadRequest(new { error = "File is not a text file." }),
            WorktreeFileWriteOutcome.WorktreeUnavailable => BadRequest(new { error = "Work item does not currently have an active worktree." }),
            _ => BadRequest(new { error = "File could not be saved." }),
        };
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

    [HttpPost("{id}/pull-branch")]
    public async Task<IActionResult> PullBranch(string id, CancellationToken cancellationToken)
    {
        var (_, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        return PullBranchHttpResult.ToActionResult(
            await _workItemManager.PullBranchAsync(id, cancellationToken));
    }

    [HttpPost("{id}/transition")]
    public async Task<IActionResult> Transition(string id, [FromBody] WorkItemTransitionRequest request)
    {
        if (!Enum.TryParse<WorkItemStatus>(request.TargetStatus, true, out var target))
            return BadRequest(new { error = "Invalid target status" });
        var ok = target switch
        {
            WorkItemStatus.Backlog => await _workItemManager.TransitionToBacklogAsync(id),
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

    [HttpPost("{id}/human-feedback/edge")]
    public async Task<IActionResult> HumanFeedbackEdge(string id, [FromBody] HumanFeedbackEdgeRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Edge name is required" });

        var ok = await _workItemManager.SubmitHumanFeedbackEdgeAsync(id, request.Name, request.Input ?? string.Empty);
        if (!ok) return NotFound();

        // SubmitHumanFeedbackEdgeAsync signals the engine which re-launches the
        // run loop along the named custom edge. See note on HumanFeedbackInput.
        return Ok();
    }

    [HttpPost("{id}/pr/merge")]
    public async Task<IActionResult> MergePr(string id, [FromBody] MergePrRequest? request = null)
    {
        var result = await _workItemManager.MergePullRequestAsync(id, request?.DeleteBranch ?? true);
        if (result == null) return NotFound();
        if (!result.Merged)
            return BadRequest(new { error = result.Error });

        // Branch deletion is best effort: surface a warning but report success
        // so the UI advances the loop just like Approve.
        return Ok(new { branchDeleted = result.BranchDeleted, warning = result.BranchWarning });
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

public class HumanFeedbackEdgeRequest
{
    /// <summary>
    /// Name of the custom edge the human selected (one of the parked node's
    /// named buttons). Routes the node to the matching <c>Custom</c> edge.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional input that becomes <c>{{PreviousNode.Output}}</c> for the
    /// custom edge's successor.
    /// </summary>
    [System.ComponentModel.DataAnnotations.StringLength(8192)]
    public string? Input { get; set; }
}

public class MergePrRequest
{
    /// <summary>
    /// When true (the default), the source branch is deleted after a
    /// successful merge. Branch deletion is best effort and never blocks the
    /// loop from continuing.
    /// </summary>
    public bool DeleteBranch { get; set; } = true;
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
