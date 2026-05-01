using System.Text;
using System.Text.Json;
using ILD.Api.Configuration;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IPrSyncService _prSync;
    private readonly AppDbContext _db;

    public WebhooksController(IPrSyncService prSync, AppDbContext db)
    {
        _prSync = prSync;
        _db = db;
    }

    [HttpPost("forgejo")]
    public async Task<IActionResult> Forgejo()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var signature = Request.Headers["X-Forgejo-Signature"].ToString();

        var secrets = await _db.RemoteProviders.AsNoTracking()
            .Where(p => p.WebhookSecret != null && p.WebhookSecret != "")
            .Select(p => p.WebhookSecret!)
            .ToListAsync();

        if (secrets.Count == 0 || !secrets.Any(s => WebhookSignatureVerifier.Verify(body, signature, s)))
            return Unauthorized();

        var payload = JsonSerializer.Deserialize<WebhookPayload>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (payload == null) return BadRequest();

        await _prSync.HandleWebhookAsync(payload);
        return Ok();
    }
}
