using ILD.Core.Services.Interfaces;
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

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _db.AiProviders.AsNoTracking().ToListAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        return p == null ? NotFound() : Ok(p);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AiProviderDto request)
    {
        var p = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Type = request.ProviderType,
            BaseUrl = request.BaseUrl,
            Model = request.DefaultModel,
            IsDefault = request.IsDefault,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiProviders.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, p);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AiProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.AiProviders.FindAsync(guid);
        if (p == null) return NotFound();
        p.Name = request.Name;
        p.Type = request.ProviderType;
        p.BaseUrl = request.BaseUrl;
        p.Model = request.DefaultModel;
        p.IsDefault = request.IsDefault;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(p);
    }
}
