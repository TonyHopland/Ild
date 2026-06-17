using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Single source of truth for the placeholder grammar used in user-authored
/// templates (AI, Human, Prompt node, PR description).
///
/// This module owns the regex, the set of known names, and the special-prefix
/// rules (<c>WorkTree.File:&lt;rel&gt;</c>). Previously the regex and the set
/// were copied across <see cref="PromptTemplateResolver"/>,
/// <see cref="LoopTemplateValidator"/>, and <see cref="AIProviderService"/>;
/// adding a placeholder meant updating three places and silently drifting if
/// any were missed.
/// </summary>
public static class PromptPlaceholderRegistry
{
    /// <summary>
    /// Names accepted by the renderer and the template validator. All
    /// comparisons are case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WorkItem.Title",
            "WorkItem.Description",
            "WorkTree.Diff",
            "EventLog.Summary",
            "EventLog.LastN",
            "Node.Input",
            "PreviousNode.Output",
        };

    /// <summary>
    /// Matches <c>{{Name}}</c> with optional inner whitespace. Names allow
    /// letters, digits, and the punctuation needed for paths and namespaces
    /// (<c>. _ : / \ -</c>).
    /// </summary>
    public static readonly Regex Pattern =
        new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    public const string WorkTreeFilePrefix = "WorkTree.File:";

    /// <summary>
    /// Prefix for per-run loop variables: <c>{{Var.&lt;name&gt;}}</c>. The name
    /// after the prefix is matched against <see cref="VariableNamePattern"/>.
    /// </summary>
    public const string VariablePrefix = "Var.";

    /// <summary>
    /// Legal loop-variable name: a letter followed by letters, digits or
    /// underscores. Kept deliberately narrow (no dots/colons) so a name can
    /// never collide with the placeholder grammar or another namespace.
    /// </summary>
    public static readonly Regex VariableNamePattern =
        new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="name"/> is a valid loop-variable name.</summary>
    public static bool IsValidVariableName(string name) => VariableNamePattern.IsMatch(name);

    /// <summary>
    /// True for any name the resolver knows how to render — a fixed known name,
    /// the <c>WorkTree.File:&lt;rel&gt;</c> prefix, or a <c>Var.&lt;name&gt;</c>
    /// loop variable with a valid name.
    /// </summary>
    public static bool IsKnown(string name)
        => KnownNames.Contains(name)
           || name.StartsWith(WorkTreeFilePrefix, StringComparison.OrdinalIgnoreCase)
           || (name.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase)
               && IsValidVariableName(name.Substring(VariablePrefix.Length)));
}
