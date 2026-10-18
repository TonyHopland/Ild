using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// How both ways a person removes a repository's preview data — the repository's
/// reset and the storage page's delete — answer: a preview holding it is a 409.
/// </summary>
internal static class PreviewDataResponse
{
    public static IActionResult From(PreviewDataDeletion deletion) => deletion.Outcome switch
    {
        PreviewDataDeletionOutcome.Deleted => new OkResult(),
        PreviewDataDeletionOutcome.Refused => new ConflictObjectResult(new { error = deletion.Error }),
        _ => new ObjectResult(new { error = deletion.Error }) { StatusCode = StatusCodes.Status500InternalServerError },
    };
}
