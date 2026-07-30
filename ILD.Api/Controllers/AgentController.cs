using ILD.Api.Contracts;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// Read-only / restricted API surface intended for the ILD MCP server.
///
/// The MCP server lets agents inside loop runs:
///  - list work items (with filters, including by originating run),
///  - list loop templates and repositories so they can pick valid ids,
///  - list loop runs so users can identify and batch-delete items
///    spawned by a specific run if an agent goes rogue,
///  - create new work items into Backlog with an optional set of
///    dependencies, stamped with the originating loop-run id,
///  - edit or delete work items the caller's own session created — the
///    item's <c>CreatedByLoopRunId</c> (or <c>CreatedByChatSessionId</c>)
///    must match the caller's session; pre-existing items and items from
///    other sessions are off-limits (403),
///  - read and write per-run loop variables (scoped by the X-ILD-Run-Id
///    header) so one node can hand off state to a later node — the values
///    are also exposed to templates as <c>{{Var.&lt;name&gt;}}</c>,
///  - pull commits pushed to their own run branch, which needs the
///    orchestrator's repository token the agent uid cannot reach (ADR-0014).
///
/// Crucially, this controller deliberately does NOT expose start,
/// transition, link-pr, or human-feedback endpoints. Agents are not
/// allowed to move work items out of Backlog; that gate is human-only.
/// </summary>
[ApiController]
[Route("api/v1/agent")]
public class AgentController : ControllerBase
{
    private const string RunIdHeader = "X-ILD-Run-Id";
    private const string ChatSessionIdHeader = "X-ILD-Chat-Session-Id";

    // Upper bound on a pushed loop document (~1 MB). A real ild-loop-template/v1 is
    // a few KB; this stops a rogue agent from shoving megabytes over the chat hub.
    private const int MaxLoopDocumentChars = 1_000_000;

    private readonly IWorkItemManager _workItems;
    private readonly ILoopTemplateManager _templates;
    private readonly ILoopRunStore _runs;
    private readonly AppDbContext _db;
    private readonly IProviderStore _providerStore;
    private readonly IWorktreePreviewService _preview;
    private readonly IChatLoopScratchpad _loopScratchpad;
    private readonly IChatNotifier _chatNotifier;
    private readonly IWorkItemNotifier _notifier;

    public AgentController(
        IWorkItemManager workItems,
        ILoopTemplateManager templates,
        ILoopRunStore runs,
        AppDbContext db,
        IProviderStore providerStore,
        IWorktreePreviewService preview,
        IChatLoopScratchpad loopScratchpad,
        IChatNotifier chatNotifier,
        IWorkItemNotifier? notifier = null)
    {
        _workItems = workItems;
        _templates = templates;
        _runs = runs;
        _db = db;
        _providerStore = providerStore;
        _preview = preview;
        _loopScratchpad = loopScratchpad;
        _chatNotifier = chatNotifier;
        _notifier = notifier ?? new NoopWorkItemNotifier();
    }

    /// <summary>
    /// Resolve a work item and its current worktree path for the preview surface,
    /// mirroring the human <c>WorkItemsController</c> gate: 404 when the item is
    /// unknown, 400 when it has no active worktree. The agent reads the open work
    /// item id from its Chat Context and passes it explicitly (consistent with
    /// <c>get_workitem</c>).
    /// </summary>
    private async Task<(WorkItemView? WorkItem, IActionResult? Error)> GetPreviewableWorkItemAsync(string id)
    {
        var workItem = await _workItems.GetWorkItemAsync(id);
        if (workItem == null)
            return (null, NotFound());
        if (string.IsNullOrWhiteSpace(workItem.WorktreePath))
            return (null, BadRequest(new { error = "Work item does not currently have an active worktree." }));
        return (workItem, null);
    }


    // Single-line description preview length for the lightweight list (~160
    // chars per the triage design): enough to recognise an item, far short of
    // its full body.
    private const int DescriptionPreviewLength = 160;

    // Upper bound on the number of dependency ids returned per row. blockedByCount
    // stays exact; only the id array is capped so a pathological item with hundreds
    // of dependencies can't bloat the (per-page) list payload.
    private const int MaxBlockedByIds = 50;

    [HttpGet("workitems")]
    public async Task<IActionResult> ListWorkItems(
        [FromQuery] string? status = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] string? createdByLoopRunId = null,
        [FromQuery] string? priority = null,
        [FromQuery] string[]? tags = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool actionableOnly = false,
        [FromQuery] bool includeDescription = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        RemoteWorkItemStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<RemoteWorkItemStatus>(status, true, out var s))
            statusFilter = s;
        RemoteWorkItemPriority? priorityFilter = null;
        if (!string.IsNullOrEmpty(priority) && Enum.TryParse<RemoteWorkItemPriority>(priority, true, out var p))
            priorityFilter = p;
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;
        Guid? runFilter = null;
        if (!string.IsNullOrEmpty(createdByLoopRunId) && Guid.TryParse(createdByLoopRunId, out var runGuid))
            runFilter = runGuid;
        var orderByValue = WorkItemOrderBy.UpdatedAt;
        if (!string.IsNullOrEmpty(orderBy) && Enum.TryParse<WorkItemOrderBy>(orderBy, true, out var ob))
            orderByValue = ob;
        var tagFilter = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        try
        {
            var items = await _workItems.ListSummariesAsync(new WorkItemListQuery
            {
                Status = statusFilter,
                Priority = priorityFilter,
                Tags = tagFilter is { Count: > 0 } ? tagFilter : null,
                RepositoryId = repoFilter,
                CreatedByLoopRunId = runFilter,
                ActionableOnly = actionableOnly,
                OrderBy = orderByValue,
                Skip = skip,
                Take = take,
            });
            return Ok(items.Select(w => new
            {
                id = w.Id,
                title = w.Title,
                status = w.Status.ToString(),
                priority = w.Priority.ToString(),
                tags = w.Tags,
                blockedBy = w.BlockedBy.Take(MaxBlockedByIds),
                blockedByCount = w.BlockedBy.Count,
                blocksCount = w.BlocksCount,
                actionable = w.IsActionable,
                repositoryId = w.RepositoryId == Guid.Empty ? null : w.RepositoryId,
                createdByLoopRunId = w.CreatedByLoopRunId,
                createdByChatSessionId = w.CreatedByChatSessionId,
                createdAt = w.CreatedAt,
                updatedAt = w.UpdatedAt,
                descriptionPreview = BuildDescriptionPreview(w.Description),
                // Full body is omitted by default (the point of the lightweight
                // list); opt back in with includeDescription for callers that
                // still want it, preserving backward compatibility.
                description = includeDescription ? w.Description : null,
            }));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable", detail = ex.Message });
        }
    }

    [HttpGet("workitems/summary")]
    public async Task<IActionResult> GetBacklogSummary([FromQuery] string? repositoryId = null)
    {
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;

        try
        {
            var summary = await _workItems.GetBacklogSummaryAsync(repoFilter);
            return Ok(new
            {
                total = summary.Total,
                countsByStatus = summary.CountsByStatus,
                countsByPriority = summary.CountsByPriority,
                blocked = summary.Blocked,
                actionable = summary.Actionable,
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable", detail = ex.Message });
        }
    }

    /// <summary>
    /// Collapse a work item body to a single-line preview of at most
    /// <see cref="DescriptionPreviewLength"/> characters (whitespace runs
    /// flattened to single spaces, truncated with an ellipsis). Returns null for
    /// an empty body so the list stays bodiless when there is nothing to show.
    /// </summary>
    private static string? BuildDescriptionPreview(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var singleLine = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();
        return singleLine.Length <= DescriptionPreviewLength
            ? singleLine
            : singleLine[..DescriptionPreviewLength].TrimEnd() + "…";
    }

    [HttpGet("workitems/{id}")]
    public async Task<IActionResult> GetWorkItem(string id, [FromQuery] bool includeConversation = false)
    {
        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        var deps = await _workItems.GetDependenciesAsync(id);
        // Reverse edges: the items that depend on this one ("blocks"). Resolved
        // here, one item at a time, so the lightweight list never pays for it.
        var blocks = await _workItems.GetDependentsAsync(id);
        return Ok(new
        {
            id = wi.Id,
            title = wi.Title,
            description = wi.Description,
            status = wi.Status.ToString(),
            priority = wi.Priority.ToString(),
            tags = wi.Tags,
            repositoryId = wi.RepositoryId == Guid.Empty ? null : (Guid?)wi.RepositoryId,
                loopTemplateVersionId = (Guid?)null,
            createdByLoopRunId = wi.CreatedByLoopRunId,
            createdByChatSessionId = wi.CreatedByChatSessionId,
            createdAt = wi.CreatedAt,
            updatedAt = wi.UpdatedAt,
            dependencies = deps.Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString() }),
            blocks = blocks.Select(b => new { id = b.Id, title = b.Title, status = b.Status.ToString() }),
            // The conversation is the largest field and rarely needed for
            // planning, so it is gated behind an explicit flag (ADR scope note).
            conversation = includeConversation
                ? wi.Conversation.Select(m => new { role = m.Role, content = m.Content, timestamp = m.Timestamp, name = m.Name })
                : null,
        });
    }

    // -- Branch sync (ADR-0014) ------------------------------------------------
    //
    // The orchestrator holds the repository token and the agent uid can reach
    // neither it nor the askpass helper, so an agent that needs commits pushed to
    // its run branch after the run started cannot `git pull` them itself. It asks
    // here instead: the orchestrator does the authenticated fetch and the local
    // rebase, and nothing about the credential crosses the uid boundary. This is
    // not a status transition — the work item stays exactly where it is — so it
    // does not breach the human-only gate the class summary describes.

    [HttpPost("workitems/{id}/pull-branch")]
    public async Task<IActionResult> PullBranch(string id, CancellationToken cancellationToken)
    {
        var (_, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;

        return PullBranchHttpResult.ToActionResult(
            await _workItems.PullBranchAsync(id, cancellationToken));
    }

    // -- Worktree preview controls (ADR-0011) ----------------------------------
    //
    // These mirror the human WorkItemsController preview surface so the chat agent
    // can drive a work item's preview. Each takes an explicit work item id (read
    // from the Chat Context). They fold under the `ild` grant. Like the human
    // controller, every mutating endpoint broadcasts PreviewStateChanged so an open
    // Preview tab live-updates when an agent starts or stops the preview.

    [HttpGet("workitems/{id}/preview")]
    public async Task<IActionResult> GetPreview(string id)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            return Ok(await _preview.GetStatusAsync(workItem!.WorktreePath!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("workitems/{id}/preview/start")]
    public async Task<IActionResult> StartPreview(string id, [FromBody] WorktreePreviewStartRequest? request)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _preview.StartAsync(
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

    [HttpPost("workitems/{id}/preview/stop")]
    public async Task<IActionResult> StopPreview(string id)
    {
        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _preview.StopAsync(workItem!.WorktreePath!);
            await _notifier.PreviewStateChangedAsync(id);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("workitems/{id}/preview/services/{service}/start")]
    public async Task<IActionResult> StartPreviewService(string id, string service, [FromBody] WorktreePreviewStartRequest? request)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _preview.StartServiceAsync(
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

    [HttpPost("workitems/{id}/preview/services/{service}/stop")]
    public async Task<IActionResult> StopPreviewService(string id, string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var response = await _preview.StopServiceAsync(workItem!.WorktreePath!, service);
            await _notifier.PreviewStateChangedAsync(id);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("workitems/{id}/preview/services/{service}/config")]
    public async Task<IActionResult> GetPreviewServiceConfig(string id, string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var config = await _preview.GetServiceConfigAsync(workItem!.WorktreePath!, service);
            if (config == null)
                return NotFound(new { error = $"No preview config found for service '{service}'." });
            return Ok(new WorktreePreviewServiceConfigResponse { Service = service, Config = config });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("workitems/{id}/preview/services/{service}/config")]
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
            await _preview.UpdateServiceConfigAsync(workItem!.WorktreePath!, service, request.Config);
            var config = await _preview.GetServiceConfigAsync(workItem!.WorktreePath!, service);
            return Ok(new WorktreePreviewServiceConfigResponse { Service = service, Config = config });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("workitems/{id}/preview/logs")]
    public async Task<IActionResult> GetPreviewLog(string id, [FromQuery] string service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return BadRequest(new { error = "service is required." });

        var (workItem, error) = await GetPreviewableWorkItemAsync(id);
        if (error != null) return error;
        try
        {
            var content = await _preview.GetServiceLogAsync(workItem!.WorktreePath!, service);
            return Ok(new WorktreePreviewLogResponse { Service = service, Content = content });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("repositories")]
    public async Task<IActionResult> ListRepositories([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var repos = await _db.Repositories.AsNoTracking()
            .OrderBy(r => r.Name)
            .Skip(skip).Take(take)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                cloneUrl = r.CloneUrl,
                defaultBranch = r.DefaultBranch,
                defaultIntakeStatus = r.DefaultIntakeStatus.ToString(),
            })
            .ToListAsync();
        return Ok(repos);
    }

    [HttpGet("loop-templates")]
    public async Task<IActionResult> ListLoopTemplates([FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] bool includeArchived = false)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var templates = await _templates.GetAllLoopTemplatesAsync(skip, take, includeArchived);
        return Ok(templates.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            description = t.Description ?? string.Empty,
            isDefault = t.IsDefault,
            isArchived = t.IsArchived,
        }));
    }

    [HttpGet("loop-runs")]
    public async Task<IActionResult> ListLoopRuns(
        [FromQuery] string? workItemId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        if (!string.IsNullOrEmpty(workItemId))
        {
            var filtered = await _db.LoopRuns.AsNoTracking()
                .Where(r => r.WorkItemId == workItemId)
                .OrderByDescending(r => r.StartedAt)
                .Skip(skip).Take(take)
                .Select(r => new { r.Id, r.WorkItemId, r.Status, r.StartedAt, r.CompletedAt })
                .ToListAsync();

            var costs = await AggregateRunCostsAsync(filtered.Select(r => r.Id).ToList());
            return Ok(filtered.Select(r => ProjectRun(r.Id, r.WorkItemId, r.Status.ToString(), r.StartedAt, r.CompletedAt, costs)));
        }

        var runs = await _runs.GetAllAsync(skip, take);
        var runList = runs.ToList();
        var allCosts = await AggregateRunCostsAsync(runList.Select(r => r.Id).ToList());
        return Ok(runList.Select(r => ProjectRun(r.Id, r.WorkItemId, r.Status.ToString(), r.StartedAt, r.CompletedAt, allCosts)));
    }

    private sealed record RunCost(decimal? CostUsd, long InputTokens, long OutputTokens);

    /// <summary>
    /// Roll up per-run token/cost totals from <c>LoopRunNode</c> rows for the
    /// given run ids in one query. Backs the read-only run cost visibility the
    /// chat agent gets (ADR-0011). Cost is null for a run whose nodes reported
    /// no monetary figure (e.g. subscription-auth providers).
    /// </summary>
    private async Task<Dictionary<Guid, RunCost>> AggregateRunCostsAsync(IReadOnlyList<Guid> runIds)
    {
        if (runIds.Count == 0) return new Dictionary<Guid, RunCost>();

        var rows = await _db.LoopRunNodes.AsNoTracking()
            .Where(n => runIds.Contains(n.LoopRunId))
            .GroupBy(n => n.LoopRunId)
            .Select(g => new
            {
                RunId = g.Key,
                CostUsd = g.Sum(n => n.CostUsd),
                InputTokens = g.Sum(n => n.InputTokens ?? 0),
                OutputTokens = g.Sum(n => n.OutputTokens ?? 0),
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.RunId, r => new RunCost(r.CostUsd, r.InputTokens, r.OutputTokens));
    }

    private static object ProjectRun(Guid id, string workItemId, string status, DateTime? startedAt, DateTime? completedAt, Dictionary<Guid, RunCost> costs)
    {
        costs.TryGetValue(id, out var cost);
        return new
        {
            id,
            workItemId,
            status,
            startedAt,
            completedAt,
            costUsd = cost?.CostUsd,
            inputTokens = cost?.InputTokens ?? 0,
            outputTokens = cost?.OutputTokens ?? 0,
        };
    }

    [HttpGet("variables")]
    public async Task<IActionResult> ListVariables()
    {
        if (!TryResolveRunId(out var runId))
            return BadRequest(new { error = $"A loop-run id is required. Send it in the {RunIdHeader} header." });

        var variables = await _runs.GetVariablesAsync(runId);
        return Ok(variables.Select(v => new
        {
            name = v.Name,
            value = v.Value,
            updatedAt = v.UpdatedAt ?? v.CreatedAt,
        }));
    }

    [HttpPut("variables/{name}")]
    public async Task<IActionResult> SetVariable(string name, [FromBody] AgentSetVariableRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!TryResolveRunId(out var runId))
            return BadRequest(new { error = $"A loop-run id is required. Send it in the {RunIdHeader} header." });

        if (!PromptPlaceholderRegistry.IsValidVariableName(name))
            return BadRequest(new { error = "Variable name must start with a letter and contain only letters, digits, and underscores." });

        var runExists = await _db.LoopRuns.AsNoTracking().AnyAsync(r => r.Id == runId);
        if (!runExists)
            return NotFound(new { error = $"Loop run not found: {runId}" });

        await _runs.SetVariableAsync(runId, name, request.Value ?? string.Empty);
        return Ok(new { name, value = request.Value ?? string.Empty });
    }

    private bool TryResolveRunId(out Guid runId)
    {
        runId = Guid.Empty;
        return Request.Headers.TryGetValue(RunIdHeader, out var hdr)
            && Guid.TryParse(hdr.ToString(), out runId);
    }

    private bool TryResolveChatSessionId(out Guid chatSessionId)
    {
        chatSessionId = Guid.Empty;
        return Request.Headers.TryGetValue(ChatSessionIdHeader, out var hdr)
            && Guid.TryParse(hdr.ToString(), out chatSessionId);
    }

    // -- Loop Editor context (ADR-0011) ----------------------------------------
    //
    // The browser stashes the loop currently open in the Loop Editor into a
    // per-session scratchpad on every chat message; the agent reads it here and
    // pushes a full-document replacement back to the open editor. Both are scoped
    // to the chat session via the X-ILD-Chat-Session-Id header. The agent is never
    // given a persist tool — update_current_loop mutates transient client state
    // only; the sole write to a LoopTemplateVersion stays the editor's human Save.

    [HttpGet("current-loop")]
    public async Task<IActionResult> GetCurrentLoop()
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        // No document stashed this turn means the Loop Editor is not open. Report it
        // as data (not an error) so the tool surfaces a clean "no loop open" notice.
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        return Content(document, "application/json");
    }

    [HttpPut("current-loop")]
    public async Task<IActionResult> UpdateCurrentLoop([FromBody] AgentLoopUpdateRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Document))
            return BadRequest(new { error = "document is required — pass a complete ild-loop-template/v1 document." });
        if (request.Document.Length > MaxLoopDocumentChars)
            return BadRequest(new { error = $"document is too large ({request.Document.Length} chars); the limit is {MaxLoopDocumentChars}." });

        // Retrofit (ADR-0011): the server now validates the full replacement itself
        // (reject-on-invalid) and returns the same synchronous ack as the scoped
        // edits. A valid document is stashed and pushed to the canvas; a rejected one
        // leaves both untouched and the agent is told now, not next turn.
        var result = LoopDocumentEditor.ReplaceDocument(request.Document);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpGet("current-loop/file")]
    public async Task<IActionResult> GetLoopFile()
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        return Content(document, "application/json");
    }

    [HttpPost("current-loop/file/edit")]
    public async Task<IActionResult> EditLoopFile([FromBody] AgentLoopFileEditRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrEmpty(request.OldString))
            return BadRequest(new { error = "old_string is required." });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.EditFile(document, request.OldString, request.NewString ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpGet("current-loop/nodes/{nodeId}")]
    public async Task<IActionResult> GetLoopNode(string nodeId)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;

        var document = _loopScratchpad.Get(chatSessionId);
        if (string.IsNullOrWhiteSpace(document))
            return Ok(new { loopEditorOpen = false });

        var (found, nodeJson, nodeError) = LoopDocumentEditor.GetNode(document, nodeId);
        if (!found)
            return NotFound(new { error = nodeError });
        return Content(nodeJson!, "application/json");
    }

    [HttpPost("current-loop/nodes/{nodeId}/edit-field")]
    public async Task<IActionResult> EditLoopNodeField(string nodeId, [FromBody] AgentLoopNodeFieldEditRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Field))
            return BadRequest(new { error = "field is required." });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.EditNodeField(
            document, nodeId, request.Field, request.OldString ?? string.Empty, request.NewString ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    [HttpPost("current-loop/nodes/{nodeId}/set-field")]
    public async Task<IActionResult> SetLoopNodeField(string nodeId, [FromBody] AgentLoopNodeFieldSetRequest? request)
    {
        var (chatSessionId, error) = await ResolveChatSessionAsync();
        if (error != null) return error;
        if (request == null || string.IsNullOrWhiteSpace(request.Field))
            return BadRequest(new { error = "field is required." });

        var notOpen = RequireOpenLoop(chatSessionId, out var document);
        if (notOpen != null) return notOpen;

        var result = LoopDocumentEditor.SetNodeField(document, nodeId, request.Field, request.Value ?? string.Empty);
        return await ApplyEditAsync(chatSessionId, result);
    }

    /// <summary>
    /// The scoped edits act on the document the browser stashed this turn. When no
    /// loop is open there is nothing to edit — reported as a clean <c>applied:false</c>
    /// ack (not an HTTP error) so the agent gets the same structured shape it gets for
    /// a bad match or a validation failure. Returns null (and the document) when a
    /// loop is open.
    /// </summary>
    private IActionResult? RequireOpenLoop(Guid chatSessionId, out string document)
    {
        document = _loopScratchpad.Get(chatSessionId) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(document))
            return null;

        return Ok(new
        {
            applied = false,
            matchCount = 0,
            validationErrors = Array.Empty<string>(),
            error = "No loop is open in the Loop Editor, so there is nothing to edit.",
        });
    }

    /// <summary>
    /// Turn a <see cref="LoopEditResult"/> into the synchronous ack every loop edit
    /// surface returns, and — only when the edit applied — stash the new document in
    /// the scratchpad and push it to the open canvas via the same
    /// <c>LoopUpdateRequested</c> path a full replacement uses. A rejected edit (bad
    /// match or failed validation) leaves both the scratchpad and the canvas exactly
    /// as they were, but the agent is told now rather than next turn (ADR-0011).
    /// </summary>
    private async Task<IActionResult> ApplyEditAsync(Guid chatSessionId, LoopEditResult result)
    {
        if (result is { Applied: true, Document: not null })
        {
            if (result.Document.Length > MaxLoopDocumentChars)
                return Ok(new
                {
                    applied = false,
                    matchCount = result.MatchCount,
                    validationErrors = Array.Empty<string>(),
                    error = $"The edited document is too large ({result.Document.Length} chars); the limit is {MaxLoopDocumentChars}.",
                    summary = (string?)null,
                });

            _loopScratchpad.Set(chatSessionId, result.Document);
            await _chatNotifier.LoopUpdateRequestedAsync(chatSessionId, result.Document);
        }

        return Ok(new
        {
            applied = result.Applied,
            matchCount = result.MatchCount,
            validationErrors = result.ValidationErrors,
            error = result.Error,
            summary = result.Summary,
        });
    }

    /// <summary>
    /// Resolve the caller's chat session for the loop-editor endpoints: the
    /// <c>X-ILD-Chat-Session-Id</c> header must be a valid GUID AND name a chat
    /// session that still exists. This is the loop-tool equivalent of
    /// <see cref="CallerOwns"/> — the MCP server is scoped to one session, so a
    /// header that resolves to no session is not the caller's to act on (403).
    /// </summary>
    private async Task<(Guid ChatSessionId, IActionResult? Error)> ResolveChatSessionAsync()
    {
        if (!TryResolveChatSessionId(out var chatSessionId))
            return (Guid.Empty, BadRequest(new { error = $"A chat-session id is required. Send it in the {ChatSessionIdHeader} header." }));

        var exists = await _db.ChatSessions.AsNoTracking().AnyAsync(c => c.Id == chatSessionId);
        if (!exists)
            return (Guid.Empty, StatusCode(403, new { error = "Unknown or inactive chat session." }));

        return (chatSessionId, null);
    }

    [HttpPost("workitems")]
    public async Task<IActionResult> CreateWorkItem([FromBody] AgentWorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // We require a real repository here. The WorkItem entity has a
        // [Required] FK to Repository, so passing null/empty would surface
        // as a "FOREIGN KEY constraint failed" database error to the agent.
        // Convert that into a clean 400 instead.
        if (!Guid.TryParse(request.RepositoryId, out var repositoryId))
            return BadRequest(new { error = "repositoryId is required. Call list_repositories first to obtain a valid id." });
        var repoExists = await _db.Repositories.AsNoTracking().AnyAsync(r => r.Id == repositoryId);
        if (!repoExists)
            return BadRequest(new { error = $"Repository not found: {repositoryId}" });

        // Legacy `loopTemplateId` (if any) is ignored — template is now
        // resolved from tags at run start (PRD §3.7).

        // Resolve originating run id: prefer body, fall back to header.
        Guid? createdByLoopRunId = null;
        if (Guid.TryParse(request.CreatedByLoopRunId, out var bodyRun))
            createdByLoopRunId = bodyRun;
        else if (Request.Headers.TryGetValue(RunIdHeader, out var hdr) && Guid.TryParse(hdr.ToString(), out var headerRun))
            createdByLoopRunId = headerRun;

        // A standalone Chat Session (ADR-0010) stamps via its own header instead.
        // It is never both: when a run id is present the chat stamp is ignored.
        Guid? createdByChatSessionId = null;
        if (createdByLoopRunId is null
            && Request.Headers.TryGetValue(ChatSessionIdHeader, out var chatHdr)
            && Guid.TryParse(chatHdr.ToString(), out var headerChat))
            createdByChatSessionId = headerChat;

        // Validate dependencies up-front so we don't half-create.
        var dependencyIds = new List<string>();
        if (request.Dependencies is { Count: > 0 })
        {
            foreach (var raw in request.Dependencies)
            {
                var dep = raw.Trim();
                if (dep.Length == 0)
                    return BadRequest(new { error = "Dependency id cannot be empty" });
                var exists = await _workItems.GetWorkItemAsync(dep) != null;
                if (!exists)
                    return BadRequest(new { error = $"Dependency not found: {dep}" });
                dependencyIds.Add(dep);
            }
        }

        string id;
        try
        {
            id = await _workItems.CreateWorkItemAsync(
                request.Title,
                request.Description ?? string.Empty,
                repositoryId,
                createdByLoopRunId,
                forceBacklog: true,
                tags: request.Tags,
                createdByChatSessionId: createdByChatSessionId);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        foreach (var dep in dependencyIds)
        {
            try { await _workItems.AddDependencyAsync(id, dep); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        var created = await _workItems.GetWorkItemAsync(id);
        return CreatedAtAction(nameof(GetWorkItem), new { id }, new
        {
            id,
            status = created?.Status.ToString(),
            createdByLoopRunId = created?.CreatedByLoopRunId,
            createdByChatSessionId = created?.CreatedByChatSessionId,
        });
    }

    [HttpPut("workitems/{id}")]
    public async Task<IActionResult> UpdateWorkItem(string id, [FromBody] AgentWorkItemUpdateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        if (!CallerOwns(wi))
            return StatusCode(403, new { error = "You can only edit work items your own session created." });

        var ok = await _workItems.UpdateAsync(id, request.Title, request.Description ?? string.Empty, request.Tags);
        if (!ok)
            return StatusCode(503, new { error = "Work item update failed." });

        var updated = await _workItems.GetWorkItemAsync(id);
        return Ok(new
        {
            id,
            title = updated?.Title,
            description = updated?.Description,
            status = updated?.Status.ToString(),
        });
    }

    [HttpDelete("workitems/{id}")]
    public async Task<IActionResult> DeleteWorkItem(string id)
    {
        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        if (!CallerOwns(wi))
            return StatusCode(403, new { error = "You can only delete work items your own session created." });

        var ok = await _workItems.DeleteAsync(id);
        if (!ok) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// True iff <paramref name="wi"/> was created by the caller's current
    /// session — the loop run identified by the <c>X-ILD-Run-Id</c> header, or
    /// (when no run id is present) the chat session identified by the
    /// <c>X-ILD-Chat-Session-Id</c> header. This is the gate that limits an
    /// agent to editing or deleting only the items it created during its own
    /// session, never pre-existing items or items from other sessions. The run
    /// id takes precedence to mirror create-time stamping (never both).
    /// </summary>
    private bool CallerOwns(WorkItemView wi)
    {
        if (TryResolveRunId(out var runId))
            return wi.CreatedByLoopRunId == runId;
        if (Request.Headers.TryGetValue(ChatSessionIdHeader, out var chatHdr)
            && Guid.TryParse(chatHdr.ToString(), out var chatId))
            return wi.CreatedByChatSessionId == chatId;
        return false;
    }
}
