using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AiProvidersController : ControllerBase
{
    private readonly IAIProviderService _aiProviderService;
    private readonly AppDbContext _db;

    public AiProvidersController(IAIProviderService aiProviderService, AppDbContext db)
    {
        _aiProviderService = aiProviderService;
        _db = db;
    }

    private static object ToResponse(AiProvider p) => new
    {
        id = p.Id,
        name = p.Name,
        type = p.Type,
        baseUrl = p.BaseUrl,
        model = p.Model,
        isDefault = p.IsDefault,
        apiKey = string.IsNullOrEmpty(p.ApiKey) ? null : "***",
        hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
        hasConfig = !string.IsNullOrEmpty(p.Config),
        supportedTools = AiToolCatalog.GetSupportedToolsForProviderType(p.Type),
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var items = await _db.AiProviders.AsNoTracking().OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync();
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        return p == null ? NotFound() : Ok(ToResponse(p));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AiProviderDto request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var p = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Type = request.Type,
            BaseUrl = request.BaseUrl,
            Model = request.Model,
            ApiKey = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey,
            IsDefault = request.IsDefault,
            Config = request.Config,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiProviders.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, ToResponse(p));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AiProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        if (p == null) return NotFound();
        p.Name = request.Name;
        p.Type = request.Type;
        p.BaseUrl = request.BaseUrl;
        p.Model = request.Model;
        if (!string.IsNullOrEmpty(request.ApiKey)) p.ApiKey = request.ApiKey;
        p.IsDefault = request.IsDefault;
        p.Config = request.Config;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(p));
    }
}
