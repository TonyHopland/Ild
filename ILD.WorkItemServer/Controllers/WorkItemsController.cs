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
        var ids = ParseIdList(activeIds);
        return Ok(await _svc.PollAsync(ids, ct));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<WorkItemDto>> Get(string id, CancellationToken ct)
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

    [HttpPut("{id}")]
    public async Task<ActionResult<WorkItemDto>> Update(string id, [FromBody] UpdateWorkItemRequest req, CancellationToken ct)
    {
        var dto = await _svc.UpdateAsync(id, req, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("{id}/transition")]
    public async Task<ActionResult<TransitionResponse>> Transition(string id, [FromBody] TransitionRequest req, CancellationToken ct)
    {
        var resp = await _svc.TransitionAsync(id, req, ct);
        if (!resp.Success && resp.Reason == "Not found") return NotFound();
        return Ok(resp);
    }

    [HttpGet("{id}/dependencies")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetDependencies(string id, CancellationToken ct)
    {
        var deps = await _svc.GetDependenciesAsync(id, ct);
        return deps == null ? NotFound() : Ok(deps);
    }

    [HttpPost("{id}/dependencies")]
    public async Task<IActionResult> AddDependency(string id, [FromBody] AddDependencyRequest req, CancellationToken ct)
        => await _svc.AddDependencyAsync(id, req.DependencyId, ct) ? NoContent() : NotFound();

    [HttpDelete("{id}/dependencies/{depId}")]
    public async Task<IActionResult> RemoveDependency(string id, string depId, CancellationToken ct)
        => await _svc.RemoveDependencyAsync(id, depId, ct) ? NoContent() : NotFound();

    [HttpPost("{id}/feedback")]
    public async Task<IActionResult> Feedback(string id, [FromBody] FeedbackRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Content)) return BadRequest("Content is required");
        return await _svc.AppendFeedbackAsync(id, req.Content, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{id}/conversation")]
    public async Task<IActionResult> AppendConversation(string id, [FromBody] AppendConversationRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Content)) return BadRequest("Content is required");
        var role = string.IsNullOrWhiteSpace(req.Role) ? "ai" : req.Role;
        return await _svc.AppendConversationAsync(id, role, req.Content, req.Name, ct) ? NoContent() : NotFound();
    }

    private static IReadOnlyList<string> ParseIdList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? Array.Empty<string>() : parts;
    }
}
