using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// The instance's private package feeds. A feed's PAT is write-only: it is taken
/// on create and on an update that sends one, and never returned — every response
/// carries a masked hint instead. User-only, like everything outside the agent API.
/// </summary>
[ApiController]
[Route("api/v1/package-feeds")]
public class PackageFeedsController : ControllerBase
{
    private readonly IPackageFeedStore _feeds;
    private readonly IConnectionTester _tester;

    public PackageFeedsController(IPackageFeedStore feeds, IConnectionTester tester)
    {
        _feeds = feeds;
        _tester = tester;
    }

    public sealed class CreateFeedRequest
    {
        public string? Name { get; set; }
        public string? FeedUrl { get; set; }
        public string? Pat { get; set; }
    }

    /// <summary>
    /// The name is the feed's identity (repositories select it by name), so an
    /// update cannot change it; a missing or empty PAT keeps the stored one.
    /// </summary>
    public sealed class UpdateFeedRequest
    {
        public string? FeedUrl { get; set; }
        public string? Pat { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var providerMissing = NuGetCredentialProvider.IsMissing(ProcessEnvironment.Current);
        return Ok((await _feeds.GetFeedsAsync(ct)).Select(f => View(f, providerMissing)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFeedRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { error = "Give the feed a name, e.g. company" });
        if (name.Length > PackageFeed.MaxNameLength)
            return BadRequest(new { error = $"Names are at most {PackageFeed.MaxNameLength} characters" });
        if (!AzureFeedUrl.TryParse(request.FeedUrl, out var url, out var problem))
            return BadRequest(new { error = problem });
        var pat = request.Pat?.Trim() ?? string.Empty;
        if (pat.Length == 0)
            return BadRequest(new { error = "Paste a PAT with Packaging (Read) scope" });
        if (pat.Length > PackageFeed.MaxPatLength)
            return BadRequest(new { error = PatTooLong });

        var normalized = PackageFeed.Normalize(name);
        if ((await _feeds.GetFeedsAsync(ct)).Any(f => f.NormalizedName == normalized))
            return BadRequest(new { error = NameTaken(name) });

        var now = DateTime.UtcNow;
        var feed = new PackageFeed
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = normalized,
            FeedUrl = url!.Url,
            Pat = pat,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _feeds.AddFeedAsync(feed, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two saves racing.
            return BadRequest(new { error = NameTaken(name) });
        }
        return CreatedAtAction(nameof(GetAll), View(feed, NuGetCredentialProvider.IsMissing(ProcessEnvironment.Current)));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFeedRequest request, CancellationToken ct)
    {
        var feed = await _feeds.GetFeedAsync(id, ct);
        if (feed is null) return NotFound();
        if (!AzureFeedUrl.TryParse(request.FeedUrl, out var url, out var problem))
            return BadRequest(new { error = problem });

        var pat = request.Pat?.Trim() ?? string.Empty;
        if (pat.Length > PackageFeed.MaxPatLength)
            return BadRequest(new { error = PatTooLong });

        feed.FeedUrl = url!.Url;
        if (pat.Length > 0)
            feed.Pat = pat;
        feed.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _feeds.UpdateFeedAsync(feed, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the read above and this save.
            return NotFound();
        }
        return Ok(View(feed, NuGetCredentialProvider.IsMissing(ProcessEnvironment.Current)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await _feeds.DeleteFeedAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var feed = await _feeds.GetFeedAsync(id, ct);
        return feed is null ? NotFound() : Ok(await _tester.TestPackageFeedAsync(feed, ct));
    }

    private static readonly string PatTooLong = $"A PAT is at most {PackageFeed.MaxPatLength} characters";

    private static string NameTaken(string name) => $"A feed named '{name}' already exists";

    // credentialProviderMissing is instance-wide, not per feed; it rides on each feed
    // so the list the Settings page already reads can warn that NuGet restores from
    // any of them will fail.
    private static object View(PackageFeed f, bool credentialProviderMissing) => new
    {
        id = f.Id,
        name = f.Name,
        feedUrl = f.FeedUrl,
        patHint = PatHint(f.Pat),
        createdAt = f.CreatedAt,
        updatedAt = f.UpdatedAt,
        credentialProviderMissing,
    };

    // Only a PAT long enough to keep most of it hidden shows its tail.
    private static string PatHint(string pat) => pat.Length < 8 ? "••••" : "••••" + pat[^4..];
}
