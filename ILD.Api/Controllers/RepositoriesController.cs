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
    private readonly IConnectionTester _connectionTester;

    public RepositoriesController(IRepositoryManager repositoryManager, AppDbContext db, IConnectionTester connectionTester)
    {
        _repositoryManager = repositoryManager;
        _db = db;
        _connectionTester = connectionTester;
    }

    // The custom .env holds secrets, so it is never echoed back in plaintext by
    // this or any other collection payload — only whether one is set (mirrors the
    // provider API-key masking). The client re-sends the full text to change it and
    // sends null/empty to keep it. The one place the plaintext is readable is the
    // dedicated GET {id}/preview-env below, which a signed-in human can call to
    // prefill the editor.
    //
    // Package feeds are listed by name with whether the feed still exists; a feed's
    // PAT never appears here.
    private static object ToResponse(Repository r, IReadOnlySet<string> existingFeeds) => new
    {
        id = r.Id,
        name = r.Name,
        remoteProviderId = r.RemoteProviderId,
        cloneUrl = r.CloneUrl,
        defaultBranch = r.DefaultBranch,
        worktreesPath = r.WorktreesPath,
        defaultIntakeStatus = r.DefaultIntakeStatus,
        hasPreviewEnv = !string.IsNullOrEmpty(r.PreviewEnv),
        packageFeeds = r.PackageFeeds
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new { name = s.Name, missing = !existingFeeds.Contains(s.NormalizedName) }),
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var items = await _db.Repositories.AsNoTracking().Include(r => r.PackageFeeds)
            .OrderBy(r => r.Name).Skip(skip).Take(take).ToListAsync();
        var existingFeeds = await ExistingFeedsAsync();
        return Ok(items.Select(r => ToResponse(r, existingFeeds)));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await FindWithFeedsAsync(guid);
        return repo == null ? NotFound() : Ok(await ResponseAsync(repo));
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
        if (await SelectFeedsAsync(repo, request.PackageFeeds) is { } feedsError)
            return BadRequest(new { error = feedsError });
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = repo.Id }, await ResponseAsync(repo));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] RepositoryDto request)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await FindWithFeedsAsync(guid);
        if (repo == null) return NotFound();
        if (await SelectFeedsAsync(repo, request.PackageFeeds) is { } feedsError)
            return BadRequest(new { error = feedsError });
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
        return Ok(await ResponseAsync(repo));
    }

    /// <summary>
    /// Replace <paramref name="repo"/>'s feed selection with <paramref name="requested"/>
    /// (feed names, any case); null keeps it as it is. Every name must be an existing
    /// feed, except one the repository already selects: a feed deleted since it was
    /// selected can be kept, to resolve again if it is recreated. Returns why the
    /// selection is refused, or null once it is staged on <paramref name="repo"/>.
    /// </summary>
    private async Task<string?> SelectFeedsAsync(Repository repo, List<string>? requested)
    {
        if (requested is null)
            return null;

        var feeds = await _db.PackageFeeds.AsNoTracking()
            .Select(f => new { f.NormalizedName, f.Name })
            .ToDictionaryAsync(f => f.NormalizedName, f => f.Name, StringComparer.Ordinal);
        var selection = new List<RepositoryPackageFeed>();
        foreach (var name in requested.Select(n => (n ?? string.Empty).Trim()).Where(n => n.Length > 0))
        {
            var normalized = PackageFeed.Normalize(name);
            if (selection.Any(s => s.NormalizedName == normalized))
                continue;
            var kept = repo.PackageFeeds.FirstOrDefault(s => s.NormalizedName == normalized);
            if (!feeds.TryGetValue(normalized, out var feedName) && kept is null)
                return $"There is no package feed named '{name}'";
            selection.Add(kept ?? new RepositoryPackageFeed { RepositoryId = repo.Id, Name = feedName!, NormalizedName = normalized });
        }

        repo.PackageFeeds.RemoveAll(s => !selection.Contains(s));
        repo.PackageFeeds.AddRange(selection.Where(s => !repo.PackageFeeds.Contains(s)));
        return null;
    }

    private Task<Repository?> FindWithFeedsAsync(Guid id)
        => _db.Repositories.Include(r => r.PackageFeeds).FirstOrDefaultAsync(r => r.Id == id);

    private async Task<IReadOnlySet<string>> ExistingFeedsAsync()
        => (await _db.PackageFeeds.AsNoTracking().Select(f => f.NormalizedName).ToListAsync()).ToHashSet(StringComparer.Ordinal);

    private async Task<object> ResponseAsync(Repository repo) => ToResponse(repo, await ExistingFeedsAsync());

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
        var repo = await FindWithFeedsAsync(guid);
        if (repo == null) return NotFound();
        repo.PreviewEnv = null;
        repo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(await ResponseAsync(repo));
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

    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid)) return BadRequest();
        var repo = await _db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == guid, cancellationToken);
        if (repo == null) return NotFound();
        var provider = await _db.RemoteProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == repo.RemoteProviderId, cancellationToken);
        return Ok(await _connectionTester.TestRepositoryAsync(repo, provider, cancellationToken));
    }
}
