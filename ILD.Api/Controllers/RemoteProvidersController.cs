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

    public RemoteProvidersController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        return Ok(await _db.RemoteProviders.AsNoTracking().OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.RemoteProviders.FindAsync(guid);
        return p == null ? NotFound() : Ok(p);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RemoteProviderDto request)
    {
        var p = new ILD.Data.Entities.RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Type = request.ProviderType,
            Url = request.BaseUrl,
            ApiKey = request.Token,
            CreatedAt = DateTime.UtcNow,
        };
        _db.RemoteProviders.Add(p);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, p);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] RemoteProviderDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var p = await _db.RemoteProviders.FindAsync(guid);
        if (p == null) return NotFound();
        p.Name = request.Name;
        p.Type = request.ProviderType;
        p.Url = request.BaseUrl;
        if (!string.IsNullOrEmpty(request.Token)) p.ApiKey = request.Token;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(p);
    }
}
