using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ILD.Api.Controllers;

/// <summary>Settings &gt; Storage: what ILD's data takes on disk, and the bulk Run Cleanup of Done work items' worktrees.</summary>
[ApiController]
[Route("api/v1/storage")]
public class StorageController : ControllerBase
{
    private readonly IStorageService _storage;

    public StorageController(IStorageService storage)
    {
        _storage = storage;
    }

    [HttpGet]
    public async Task<ActionResult<StorageOverview>> GetOverview()
        => await _storage.GetOverviewAsync(HttpContext.RequestAborted);

    [HttpGet("worktree-cleanup")]
    public async Task<IActionResult> GetCleanupCandidates()
    {
        try
        {
            return Ok(await _storage.GetCleanupCandidatesAsync(HttpContext.RequestAborted));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable: whether the work items are Done could not be checked. Retry later.", detail = ex.Message });
        }
    }

    public sealed class WorktreeCleanupRequest
    {
        // Strings, so an id that is not a GUID is refused as { error } rather than by the model binder.
        public List<string>? RunIds { get; set; }
    }

    [HttpPost("worktree-cleanup")]
    public async Task<IActionResult> CleanUpWorktrees([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WorktreeCleanupRequest? request)
    {
        if (request?.RunIds is not { Count: > 0 } ids)
            return BadRequest(new { error = "runIds must list at least one run." });
        var runIds = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var runId))
                return BadRequest(new { error = $"Invalid run GUID: '{id}'." });
            runIds.Add(runId);
        }

        try
        {
            return Ok(await _storage.CleanUpWorktreesAsync(runIds));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable: no worktree was cleaned up. Retry later.", detail = ex.Message });
        }
    }
}
