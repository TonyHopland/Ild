namespace ILD.Data;

/// <summary>
/// The one owner of how several model ids are kept in a single string (<c>LoopRunNode.Model</c>):
/// a visit can spend tokens on more than one model, such as a main agent and its sub-agents.
/// The ids are separated by a comma, which no model id contains; entries are trimmed on the way out.
/// </summary>
public static class ModelIds
{
    private const char Separator = ',';

    /// <summary>The ids as one string, in the order given; null when there are none.</summary>
    public static string? Join(IEnumerable<string> ids)
    {
        var kept = ids.Select(id => id.Trim()).Where(id => id.Length > 0).ToList();
        return kept.Count == 0 ? null : string.Join(Separator, kept);
    }

    /// <summary>The ids in a string made by <see cref="Join"/>; empty for null or blank.</summary>
    public static IReadOnlyList<string> Split(string? joined)
        => string.IsNullOrWhiteSpace(joined)
            ? []
            : joined.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
