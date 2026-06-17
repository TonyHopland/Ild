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
    /// True for any name the resolver knows how to render — either a fixed
    /// known name or the <c>WorkTree.File:&lt;rel&gt;</c> dynamic prefix.
    /// </summary>
    public static bool IsKnown(string name)
        => KnownNames.Contains(name)
           || name.StartsWith(WorkTreeFilePrefix, StringComparison.OrdinalIgnoreCase);
}
