using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
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

    // The custom .env holds secrets, so it is never echoed back in plaintext by
    // this or any other collection payload — only whether one is set (mirrors the
    // provider API-key masking). The client re-sends the full text to change it and
    // sends null/empty to keep it. The one place the plaintext is readable is the
    // dedicated GET {id}/preview-env below, which a signed-in human can call to
    // prefill the editor.
    private static object ToResponse(Repository r) => new
    {
        id = r.Id,
        name = r.Name,
        remoteProviderId = r.RemoteProviderId,
        cloneUrl = r.CloneUrl,
        defaultBranch = r.DefaultBranch,
        worktreesPath = r.WorktreesPath,
        defaultIntakeStatus = r.DefaultIntakeStatus,
        hasPreviewEnv = !string.IsNullOrEmpty(r.PreviewEnv),
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var items = await _db.Repositories.AsNoTracking().OrderBy(r => r.Name).Skip(skip).Take(take).ToListAsync();
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.FindAsync(guid);
        return repo == null ? NotFound() : Ok(ToResponse(repo));
    }

    [HttpPost("inspect-remote")]
    public async Task<IActionResult> InspectRemote([FromBody] InspectRemoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CloneUrl))
            return BadRequest(new { error = "CloneUrl is required" });

        GitAuthOptions? auth = null;
        if (Guid.TryParse(request.RemoteProviderId, out var providerId))
        {
            var provider = await _db.RemoteProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
            if (provider != null)
                auth = new GitAuthOptions(request.CloneUrl, provider.ApiKey, provider.Type);
        }

        // Degrade gracefully: an unfetchable remote yields a null info, which we
        // return as empty fields so the user just fills them in by hand.
        var info = await _repositoryManager.InspectRemoteAsync(request.CloneUrl, auth: auth);
        return Ok(new InspectRemoteResponse
        {
            Name = info?.Name,
            DefaultBranch = info?.DefaultBranch,
        });
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
            WorktreesPath = request.WorktreesPath,
            RemoteProviderId = providerId,
            DefaultIntakeStatus = request.DefaultIntakeStatus,
            PreviewEnv = string.IsNullOrEmpty(request.PreviewEnv) ? null : request.PreviewEnv,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = repo.Id }, ToResponse(repo));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] RepositoryDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.FindAsync(guid);
        if (repo == null) return NotFound();
        repo.Name = request.Name;
        repo.CloneUrl = request.CloneUrl;
        repo.DefaultBranch = request.DefaultBranch;
        repo.WorktreesPath = request.WorktreesPath;
        repo.DefaultIntakeStatus = request.DefaultIntakeStatus;
        // Masked field: only overwrite when the client sends a new value, so a save
        // that leaves the textarea blank keeps the stored .env (mirrors ApiKey).
        if (!string.IsNullOrEmpty(request.PreviewEnv)) repo.PreviewEnv = request.PreviewEnv;
        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(repo));
    }

    // Reading and clearing the custom .env sit on their own sub-resource rather than
    // widening the repository payload: one narrow, auditable surface. Like every
    // endpoint outside AgentController it is user-only under the fallback policy,
    // so an agent presenting the service token gets a 403 here.

    [HttpGet("{id}/preview-env")]
    public async Task<IActionResult> GetPreviewEnv(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == guid);
        // Decrypted transparently on read by the EF value converter.
        return repo == null ? NotFound() : Ok(new { previewEnv = repo.PreviewEnv });
    }

    // The PUT above treats an empty PreviewEnv as "keep what is stored", so removing
    // the .env needs its own verb; the editor prefills the real text and calls this
    // when the user empties it.
    [HttpDelete("{id}/preview-env")]
    public async Task<IActionResult> ClearPreviewEnv(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.FindAsync(guid);
        if (repo == null) return NotFound();
        repo.PreviewEnv = null;
        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(repo));
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
