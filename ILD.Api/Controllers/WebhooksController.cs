using ILD.Core.DTOs;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IPrSyncService _prSync;

    public WebhooksController(IPrSyncService prSync)
    {
        _prSync = prSync;
    }

    [HttpPost("forgejo")]
    public async Task<IActionResult> Forgejo([FromBody] WebhookPayload payload)
    {
        await _prSync.HandleWebhookAsync(payload);
        return Ok();
    }
}
