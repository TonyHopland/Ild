using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Single source of truth for the placeholder grammar used in user-authored
/// templates (AI, Human, Prompt node, PR description).
///
/// This module owns the regex, the set of known names, and the special-prefix
/// rules (<c>WorkTree.File:&lt;rel&gt;</c>). Previously the regex and the set
/// were copied into each consumer, such as <see cref="PromptTemplateResolver"/>
/// and <see cref="LoopTemplateValidator"/>; adding a placeholder meant updating
/// every copy and silently drifting if any were missed.
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
            "Conversation.Full",
            "Conversation.AI",
            "Conversation.Human",
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
    /// Legal loop-variable name: a letter followed by 0..127 letters, digits or
    /// underscores. Kept deliberately narrow (no dots/colons) so a name can
    /// never collide with the placeholder grammar or another namespace, and
    /// upper-bounded at 128 chars to match the <c>LoopRunVariable.Name</c>
    /// column so a pattern-valid name can never overflow it at write time.
    /// </summary>
    public static readonly Regex VariableNamePattern =
        new(@"^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="name"/> is a valid loop-variable name.</summary>
    public static bool IsValidVariableName(string name) => VariableNamePattern.IsMatch(name);

    /// <summary>
    /// Prefix for instance-wide global variables: <c>{{Global.&lt;name&gt;}}</c>.
    /// The name follows the loop-variable rule (<see cref="VariableNamePattern"/>),
    /// but the two namespaces never read each other.
    /// </summary>
    public const string GlobalPrefix = "Global.";

    /// <summary>
    /// The global-variable names <paramref name="template"/> uses, distinct
    /// ignoring case, in order of first appearance. Malformed names are not
    /// placeholders, so they are not listed.
    /// </summary>
    public static IReadOnlyList<string> GlobalVariableNames(string? template)
    {
        if (string.IsNullOrEmpty(template)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (Match m in Pattern.Matches(template))
        {
            if (TryGlobalName(m.Groups[1].Value, out var name) && seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>True when <paramref name="key"/> is <c>Global.&lt;name&gt;</c> with a valid name.</summary>
    public static bool TryGlobalName(string key, out string name)
    {
        name = key.StartsWith(GlobalPrefix, StringComparison.OrdinalIgnoreCase) ? key.Substring(GlobalPrefix.Length) : "";
        return IsValidVariableName(name);
    }

    /// <summary>
    /// True for any name the resolver knows how to render — a fixed known name,
    /// the <c>WorkTree.File:&lt;rel&gt;</c> prefix, a <c>Var.&lt;name&gt;</c>
    /// loop variable or a <c>Global.&lt;name&gt;</c> global variable with a valid name.
    /// Whether such a global exists is not checked: that is the engine's halt.
    /// </summary>
    public static bool IsKnown(string name)
        => KnownNames.Contains(name)
           || name.StartsWith(WorkTreeFilePrefix, StringComparison.OrdinalIgnoreCase)
           || (name.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase)
               && IsValidVariableName(name.Substring(VariablePrefix.Length)))
           || TryGlobalName(name, out _);
}
