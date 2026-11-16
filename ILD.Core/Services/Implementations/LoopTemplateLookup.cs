using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class LoopTemplateLookup : ILoopTemplateLookup
{
    /// <summary>What <see cref="string.Trim()"/> trims, spelled out: SQL's own trim takes spaces only.</summary>
    private static readonly char[] Whitespace = Enumerable.Range(char.MinValue, char.MaxValue + 1)
        .Select(c => (char)c).Where(char.IsWhiteSpace).ToArray();

    private readonly AppDbContext _db;

    public LoopTemplateLookup(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LoopTemplateLookupResult> FindAsync(string nameOrId)
    {
        var key = nameOrId.Trim();
        // An id wins; a value that only looks like one may still be a template's name.
        var template = Guid.TryParse(key, out var templateId)
            ? await _db.LoopTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId)
            : null;
        if (template != null)
            return new(LoopTemplateLookupOutcome.Found, template);

        var lowered = key.ToLowerInvariant();
        var named = await _db.LoopTemplates.AsNoTracking()
            .Where(t => t.Name.Trim(Whitespace).ToLower() == lowered)
            .ToListAsync();
        return named.Count switch
        {
            0 => new(LoopTemplateLookupOutcome.NotFound, Error: $"Loop template not found: {key}"),
            1 => new(LoopTemplateLookupOutcome.Found, named[0]),
            _ => new(LoopTemplateLookupOutcome.Ambiguous, Error: $"{named.Count} loop templates are named '{key}'; pass the id of the one you mean.", Candidates: named),
        };
    }
}
