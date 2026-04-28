using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
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

    public WorkItemsController(IWorkItemManager workItemManager, ILoopEngine engine, AppDbContext db)
    {
        _workItemManager = workItemManager;
        _engine = engine;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? status = null)
    {
        IQueryable<WorkItem> q = _db.WorkItems.AsNoTracking();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<WorkItemStatus>(status, true, out var s))
            q = q.Where(w => w.Status == s);
        var items = await q.OrderByDescending(w => w.CreatedAt).ToListAsync();
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

        return CreatedAtAction(nameof(GetById), new { id }, new { id });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WorkItemCreateRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var wi = await _db.WorkItems.FindAsync(guid);
        if (wi == null) return NotFound();
        wi.Title = request.Title;
        wi.Description = request.Description;
        wi.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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
    public async Task<IActionResult> GetDependencies(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var dependencies = await _workItemManager.GetDependenciesAsync(guid);
        return Ok(dependencies);
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
    public async Task<IActionResult> GetRuns(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var runs = await _db.LoopRuns.AsNoTracking().Where(r => r.WorkItemId == guid).OrderByDescending(r => r.StartedAt).ToListAsync();
        return Ok(runs);
    }

    [HttpPost("{id}/mark-merged")]
    public async Task<IActionResult> MarkMerged(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var ok = await _workItemManager.ManuallyMarkMergedAsync(guid);
        return ok ? Ok() : NotFound();
    }
}

public class AddDependencyRequest
{
    public string DependencyId { get; set; } = string.Empty;
}
