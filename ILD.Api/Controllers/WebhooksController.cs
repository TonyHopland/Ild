using System.Text;
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
    private readonly IReadOnlyDictionary<string, IRemoteGitProviderAdapter> _adapters;

    public WebhooksController(IPrSyncService prSync, AppDbContext db, IEnumerable<IRemoteGitProviderAdapter> adapters)
    {
        _prSync = prSync;
        _db = db;
        _adapters = adapters.ToDictionary(a => a.WebhookRouteSegment, StringComparer.OrdinalIgnoreCase);
    }

    [HttpPost("forgejo")]
    public Task<IActionResult> Forgejo()
        => HandleAsync("forgejo");

    [HttpPost("github")]
    public Task<IActionResult> GitHub()
        => HandleAsync("github");

    private async Task<IActionResult> HandleAsync(string routeSegment)
    {
        if (!_adapters.TryGetValue(routeSegment, out var adapter))
            return NotFound();

        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var headers = Request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        var secrets = await _db.RemoteProviders.AsNoTracking()
            .Where(p => p.Type == adapter.ProviderType && p.WebhookSecret != null && p.WebhookSecret != "")
            .Select(p => p.WebhookSecret!)
            .ToListAsync();

        if (secrets.Count == 0 || !secrets.Any(secret => adapter.VerifyWebhookSignature(body, headers, secret)))
            return Unauthorized();

        var payload = adapter.ParseWebhookPayload(body, headers);
        if (payload == null)
            return Ok();

        await _prSync.HandleWebhookAsync(payload);
        return Ok();
    }
}
