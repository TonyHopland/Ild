using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RemoteProvidersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IRemoteProviderTypeCatalog _providerTypes;

    public RemoteProvidersController(AppDbContext db, IRemoteProviderTypeCatalog providerTypes)
    {
        _db = db;
        _providerTypes = providerTypes;
    }

    private static object ToResponse(ILD.Data.Entities.RemoteProvider p) => new
    {
        id = p.Id,
        name = p.Name,
        type = p.Type,
        baseUrl = p.Url,
        webhookSecret = p.WebhookSecret,
        apiKey = string.IsNullOrEmpty(p.ApiKey) ? null : "***",
        hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
        isDefault = p.IsDefault,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var items = await _db.RemoteProviders.AsNoTracking().OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync();
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("types")]
    public IActionResult GetTypes()
        => Ok(_providerTypes.GetAvailableTypes().Select(type => new { type }));

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.RemoteProviders.FindAsync(guid);
        return p == null ? NotFound() : Ok(ToResponse(p));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RemoteProviderDto request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!_providerTypes.IsSupported(request.Type))
            return BadRequest(new { error = $"Unsupported remote provider type '{request.Type}'." });

        var p = new ILD.Data.Entities.RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Type = request.Type,
            Url = request.BaseUrl,
            ApiKey = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey,
            WebhookSecret = string.IsNullOrEmpty(request.WebhookSecret) ? null : request.WebhookSecret,
            IsDefault = request.IsDefault,
            CreatedAt = DateTime.UtcNow,
        };
        _db.RemoteProviders.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, ToResponse(p));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] RemoteProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.RemoteProviders.FindAsync(guid);
        if (p == null) return NotFound();
        if (!_providerTypes.IsSupported(request.Type))
            return BadRequest(new { error = $"Unsupported remote provider type '{request.Type}'." });
        p.Name = request.Name;
        p.Type = request.Type;
        p.Url = request.BaseUrl;
        if (!string.IsNullOrEmpty(request.ApiKey)) p.ApiKey = request.ApiKey;
        if (!string.IsNullOrEmpty(request.WebhookSecret)) p.WebhookSecret = request.WebhookSecret;
        p.IsDefault = request.IsDefault;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(p));
    }
}
