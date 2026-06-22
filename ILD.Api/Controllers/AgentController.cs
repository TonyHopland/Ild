using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
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
///    are also exposed to templates as <c>{{Var.&lt;name&gt;}}</c>.
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

    private readonly IWorkItemManager _workItems;
    private readonly ILoopTemplateManager _templates;
    private readonly ILoopRunStore _runs;
    private readonly AppDbContext _db;
    private readonly IWorktreePreviewService _preview;

    public AgentController(
        IWorkItemManager workItems,
        ILoopTemplateManager templates,
        ILoopRunStore runs,
        AppDbContext db,
        IWorktreePreviewService preview)
    {
        _workItems = workItems;
        _templates = templates;
        _runs = runs;
        _db = db;
        _preview = preview;
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

    [HttpGet("workitems")]
    public async Task<IActionResult> ListWorkItems(
        [FromQuery] string? status = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] string? createdByLoopRunId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        RemoteWorkItemStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<RemoteWorkItemStatus>(status, true, out var s))
            statusFilter = s;
        Guid? repoFilter = null;
        if (!string.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, out var repoGuid))
            repoFilter = repoGuid;
        Guid? runFilter = null;
        if (!string.IsNullOrEmpty(createdByLoopRunId) && Guid.TryParse(createdByLoopRunId, out var runGuid))
            runFilter = runGuid;

        try
        {
            var items = await _workItems.ListAsync(statusFilter, runFilter, repoFilter, skip, take);
            return Ok(items.Select(w => new
            {
                id = w.Id,
                title = w.Title,
                description = w.Description,
                status = w.Status.ToString(),
                repositoryId = w.RepositoryId == Guid.Empty ? null : (Guid?)w.RepositoryId,
                loopTemplateVersionId = (Guid?)null,
                createdByLoopRunId = w.CreatedByLoopRunId,
                createdByChatSessionId = w.CreatedByChatSessionId,
                createdAt = w.CreatedAt,
                updatedAt = w.UpdatedAt,
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

    [HttpGet("workitems/{id}")]
    public async Task<IActionResult> GetWorkItem(string id)
    {
        var wi = await _workItems.GetWorkItemAsync(id);
        if (wi == null) return NotFound();
        var deps = await _workItems.GetDependenciesAsync(id);
        return Ok(new
        {
            id = wi.Id,
            title = wi.Title,
            description = wi.Description,
            status = wi.Status.ToString(),
            repositoryId = wi.RepositoryId == Guid.Empty ? null : (Guid?)wi.RepositoryId,
                loopTemplateVersionId = (Guid?)null,
            createdByLoopRunId = wi.CreatedByLoopRunId,
            createdByChatSessionId = wi.CreatedByChatSessionId,
            createdAt = wi.CreatedAt,
            updatedAt = wi.UpdatedAt,
            dependencies = deps.Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString() }),
        });
    }

    // -- Worktree preview controls (ADR-0011) ----------------------------------
    //
    // These mirror the human WorkItemsController preview surface so the chat agent
    // can drive a work item's preview. Each takes an explicit work item id (read
    // from the Chat Context). They fold under the `ild` grant; unlike the human
    // controller they raise no SignalR notifications — agents act headless.

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
                    request?.PortOverrides));
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
            return Ok(await _preview.StopAsync(workItem!.WorktreePath!));
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
                    request?.PortOverrides));
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
            return Ok(await _preview.StopServiceAsync(workItem!.WorktreePath!, service));
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
