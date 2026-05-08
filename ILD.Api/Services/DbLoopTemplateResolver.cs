using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Services;

/// <summary>
/// Loop template resolver backed by the local <see cref="AppDbContext"/>:
/// a tag matches a loop template iff <see cref="ILD.Data.Entities.LoopTemplate.Name"/>
/// equals the tag (case-insensitive). Only non-archived templates are considered.
/// </summary>
public sealed class DbLoopTemplateResolver : ILoopTemplateResolver
{
    private readonly AppDbContext _db;

    public DbLoopTemplateResolver(AppDbContext db) => _db = db;

    public LoopTemplateResolution Resolve(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>());

        var lowered = tags.Select(t => t.ToLowerInvariant()).ToHashSet();
        var matches = _db.LoopTemplates
            .Where(t => !t.IsArchived && lowered.Contains(t.Name.ToLower()))
            .Select(t => new { t.Id, t.Name })
            .ToList();

        if (matches.Count == 0)
            return new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>());
        if (matches.Count == 1)
            return new LoopTemplateResolution(LoopTemplateResolutionKind.Single, matches[0].Id, new[] { matches[0].Name });

        return new LoopTemplateResolution(
            LoopTemplateResolutionKind.Ambiguous,
            null,
            matches.Select(m => m.Name).ToArray());
    }
}
