using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>Settings &gt; Storage: what ILD's data takes on disk, and removing a repository's preview data.</summary>
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
