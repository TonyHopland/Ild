using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ILD.WorkItemServer.Controllers;

[ApiController]
[Route("workitems")]
public sealed class WorkItemsController : ControllerBase
{
    private readonly IWorkItemService _svc;

    public WorkItemsController(IWorkItemService svc) => _svc = svc;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkItemDto>>> List(
        [FromQuery] WorkItemStatus? status,
        [FromQuery] string? tags,
        CancellationToken ct)
    {
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Ok(await _svc.ListAsync(status, tagList, ct));
    }

    [HttpGet("poll")]
    public async Task<ActionResult<PollResponse>> Poll(
        [FromQuery] string? activeIds,
        CancellationToken ct)
    {
        var ids = ParseGuidList(activeIds);
        return Ok(await _svc.PollAsync(ids, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkItemDto>> Get(Guid id, CancellationToken ct)
    {
        var w = await _svc.GetAsync(id, ct);
        return w == null ? NotFound() : Ok(w);
    }

    [HttpPost]
    public async Task<ActionResult<WorkItemDto>> Create([FromBody] CreateWorkItemRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("Title is required");
        var dto = await _svc.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WorkItemDto>> Update(Guid id, [FromBody] UpdateWorkItemRequest req, CancellationToken ct)
    {
        var dto = await _svc.UpdateAsync(id, req, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/transition")]
    public async Task<ActionResult<TransitionResponse>> Transition(Guid id, [FromBody] TransitionRequest req, CancellationToken ct)
    {
        var resp = await _svc.TransitionAsync(id, req, ct);
        if (!resp.Success && resp.Reason == "Not found") return NotFound();
        return Ok(resp);
    }

    [HttpGet("{id:guid}/dependencies")]
    public async Task<ActionResult<IReadOnlyList<Guid>>> GetDependencies(Guid id, CancellationToken ct)
    {
        var deps = await _svc.GetDependenciesAsync(id, ct);
        return deps == null ? NotFound() : Ok(deps);
    }

    [HttpPost("{id:guid}/dependencies")]
    public async Task<IActionResult> AddDependency(Guid id, [FromBody] AddDependencyRequest req, CancellationToken ct)
        => await _svc.AddDependencyAsync(id, req.DependencyId, ct) ? NoContent() : NotFound();

    [HttpDelete("{id:guid}/dependencies/{depId:guid}")]
    public async Task<IActionResult> RemoveDependency(Guid id, Guid depId, CancellationToken ct)
        => await _svc.RemoveDependencyAsync(id, depId, ct) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid id, [FromBody] FeedbackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Content)) return BadRequest("Content is required");
        return await _svc.AppendFeedbackAsync(id, req.Content, ct) ? NoContent() : NotFound();
    }

    private static IReadOnlyList<Guid> ParseGuidList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<Guid>();
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<Guid>(parts.Length);
        foreach (var p in parts)
        {
            if (Guid.TryParse(p, out var g)) result.Add(g);
        }
        return result;
    }
}
