using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ILD.Api.Controllers;

/// <summary>Settings &gt; Storage: what ILD's data takes on disk, and the bulk Run Cleanup of Done work items' worktrees.</summary>
[ApiController]
[Route("api/v1/storage")]
public class StorageController : ControllerBase
{
    private readonly IStorageService _storage;
    private readonly IPreviewDataService _previewData;

    public StorageController(IStorageService storage, IPreviewDataService previewData)
    {
        _storage = storage;
        _previewData = previewData;
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
        catch (Exception ex) when (WorkItemServerUnreachable.Is(ex))
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
        catch (Exception ex) when (WorkItemServerUnreachable.Is(ex))
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable: no worktree was cleaned up. Retry later.", detail = ex.Message });
        }
    }

    // The one way a person removes a repository's preview data: the storage page and the
    // repository's Reset both call it. No repository lookup, since the folder of a deleted
    // repository can only be removed here. A preview holding the data is a 409.
    [HttpDelete("preview-data/{repositoryId}")]
    public async Task<IActionResult> DeletePreviewData(string repositoryId)
    {
        if (!Guid.TryParse(repositoryId, out var id))
            return BadRequest(new { error = $"Invalid repository GUID: '{repositoryId}'." });
        var deletion = await _previewData.DeleteAsync(id);
        return deletion.Outcome switch
        {
            PreviewDataDeletionOutcome.Deleted => Ok(),
            PreviewDataDeletionOutcome.Refused => Conflict(new { error = deletion.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = deletion.Error }),
        };
    }
}
