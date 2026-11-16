using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The loop template an agent names by id or by name: the id when it is one, else the exact name (case and
/// surrounding whitespace ignored, archived templates included).
/// </summary>
public interface ILoopTemplateLookup
{
    Task<LoopTemplateLookupResult> FindAsync(string nameOrId);
}

public enum LoopTemplateLookupOutcome
{
    Found,
    NotFound,

    /// <summary>More than one template has the name.</summary>
    Ambiguous,
}

/// <param name="Error">Why nothing was found, for the caller to say; null when a template was.</param>
/// <param name="Candidates">The templates sharing the name; set when it was ambiguous.</param>
public sealed record LoopTemplateLookupResult(
    LoopTemplateLookupOutcome Outcome,
    LoopTemplate? Template = null,
    string? Error = null,
    IReadOnlyList<LoopTemplate>? Candidates = null);
