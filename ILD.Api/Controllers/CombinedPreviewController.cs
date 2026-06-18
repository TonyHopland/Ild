using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// Endpoints for the "Preview together" flow: compose several work items' current
/// run branches into one throwaway integration worktree and run a single preview
/// over the set. The integration branch is test-only and never delivered.
/// </summary>
[ApiController]
[Route("api/v1/combined-preview")]
public class CombinedPreviewController : ControllerBase
{
    private readonly ICombinedPreviewService _service;
    private readonly IWorkItemNotifier _notifier;

    public CombinedPreviewController(ICombinedPreviewService service, IWorkItemNotifier? notifier = null)
    {
        _service = service;
        _notifier = notifier ?? new NoopWorkItemNotifier();
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? ids)
    {
        var workItemIds = ParseIds(ids);
        if (workItemIds.Count == 0)
            return BadRequest(new { error = "ids query parameter is required." });
        try
        {
            return Ok(await _service.GetAsync(workItemIds));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] CombinedPreviewStartRequest request)
    {
        if (request.WorkItemIds.Count == 0)
            return BadRequest(new { error = "workItemIds is required." });
        try
        {
            var response = await _service.StartAsync(request);
            await NotifyMembersAsync(request.WorkItemIds);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromBody] CombinedPreviewRequest request)
    {
        if (request.WorkItemIds.Count == 0)
            return BadRequest(new { error = "workItemIds is required." });
        try
        {
            var response = await _service.StopAsync(request.WorkItemIds);
            await NotifyMembersAsync(request.WorkItemIds);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task NotifyMembersAsync(IEnumerable<string> workItemIds)
    {
        foreach (var id in workItemIds)
            await _notifier.PreviewStateChangedAsync(id);
    }

    private static List<string> ParseIds(string? ids)
        => string.IsNullOrWhiteSpace(ids)
            ? new List<string>()
            : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
