using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using ILD.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RepositoriesController : ControllerBase
{
    private readonly IRepositoryManager _repositoryManager;
    private readonly AppDbContext _db;

    public RepositoriesController(IRepositoryManager repositoryManager, AppDbContext db)
    {
        _repositoryManager = repositoryManager;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _db.Repositories.AsNoTracking().ToListAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.FindAsync(guid);
        return repo == null ? NotFound() : Ok(repo);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RepositoryDto request)
    {
        if (!Guid.TryParse(request.RemoteProviderId, out var providerId))
            return BadRequest(new { error = "Invalid RemoteProviderId" });
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CloneUrl = request.CloneUrl,
            DefaultBranch = request.DefaultBranch,
            RemoteProviderId = providerId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = repo.Id }, repo);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.FindAsync(guid);
        if (repo == null) return NotFound();
        _db.Repositories.Remove(repo);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
