using ILD.Core.Services.Remote;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Contracts;

/// <summary>
/// The single HTTP shaping of an <see cref="AttachmentUploadResult"/>. The
/// WorkItem server is the only place an item's stored total is known, so its
/// refusal arrives here as an outcome and must stay a 400 carrying that reason —
/// every other failure of a call to it is reported as a 503 outage, which would
/// tell a user their instance is down when their upload was simply too big.
/// </summary>
public static class AttachmentHttpResult
{
    public static IActionResult ToActionResult(AttachmentUploadResult result) => result.Outcome switch
    {
        AttachmentUploadOutcome.Created => new ObjectResult(result.Created) { StatusCode = StatusCodes.Status201Created },
        AttachmentUploadOutcome.NotFound => new NotFoundResult(),
        _ => new BadRequestObjectResult(new { error = result.Error }),
    };
}
