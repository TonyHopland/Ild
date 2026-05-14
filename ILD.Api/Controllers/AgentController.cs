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
///    dependencies, stamped with the originating loop-run id.
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

    private readonly IWorkItemManager _workItems;
    private readonly ILoopTemplateManager _templates;
    private readonly ILoopRunStore _runs;
    private readonly AppDbContext _db;

    public AgentController(
        IWorkItemManager workItems,
        ILoopTemplateManager templates,
        ILoopRunStore runs,
        AppDbContext db)
    {
        _workItems = workItems;
        _templates = templates;
        _runs = runs;
        _db = db;
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
            createdAt = wi.CreatedAt,
            updatedAt = wi.UpdatedAt,
            dependencies = deps.Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString() }),
        });
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
                .Select(r => new
                {
                    id = r.Id,
                    workItemId = r.WorkItemId,
                    status = r.Status.ToString(),
                    startedAt = r.StartedAt,
                    completedAt = r.CompletedAt,
                })
                .ToListAsync();
            return Ok(filtered);
        }

        var runs = await _runs.GetAllAsync(skip, take);
        return Ok(runs.Select(r => new
        {
            id = r.Id,
            workItemId = r.WorkItemId,
            status = r.Status.ToString(),
            startedAt = r.StartedAt,
            completedAt = r.CompletedAt,
        }));
    }

    [HttpPost("workitems")]
    public async Task<IActionResult> CreateWorkItem([FromBody] AgentWorkItemCreateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // We require a real repository here. The WorkItem entity has a
        // [Required] FK to Repository, so passing null/empty would surface
        // as a "FOREIGN KEY constraint failed" SQLite error to the agent.
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
                tags: request.Tags);
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
        });
    }
}
