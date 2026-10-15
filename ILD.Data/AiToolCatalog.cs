using ILD.Data.DTOs;

namespace ILD.Data;

public static class AiToolCatalog
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Execute = "execute";
    public const string Ild = "ild";

    private static readonly AiToolDefinition IldTool =
        new(Ild, "Ild", "Use ILD-specific tools such as work item and loop APIs.");

    /// <summary>Workspace read/write/execute plus ILD's own tools, all on by default.</summary>
    public static readonly IReadOnlyList<AiToolDefinition> DefaultTools =
    [
        new(Read, "Read", "Read files and inspect the workspace."),
        new(Write, "Write", "Edit and create files in the workspace."),
        new(Execute, "Execute", "Run shell commands in the workspace."),
        IldTool,
    ];

    /// <summary>Only ILD's own tools, for a CLI whose workspace tools ILD cannot turn on or off.</summary>
    public static readonly IReadOnlyList<AiToolDefinition> IldOnlyTools = [IldTool];

    public static IReadOnlyList<string> GetDefaultToolKeys(AgentAdapterTraits traits)
        => traits.SupportedTools
            .Where(tool => tool.DefaultEnabled)
            .Select(tool => tool.Key)
            .ToArray();

    /// <summary>
    /// The supported keys of <paramref name="selectedToolKeys"/>. A null selection
    /// gets the type's defaults, and so does one left empty once unsupported keys
    /// are dropped, unless <see cref="AgentAdapterTraits.EmptyToolSelectionMeansNone"/>.
    /// </summary>
    public static IReadOnlyList<string> NormalizeSelectedToolKeys(AgentAdapterTraits traits, IEnumerable<string?>? selectedToolKeys)
    {
        if (traits.SupportedTools.Count == 0)
            return Array.Empty<string>();

        if (selectedToolKeys is null)
            return GetDefaultToolKeys(traits);

        var supportedKeys = new HashSet<string>(traits.SupportedTools.Select(tool => tool.Key), StringComparer.OrdinalIgnoreCase);
        var requested = selectedToolKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(supportedKeys.Contains)
            .ToArray();

        return requested.Length > 0 || traits.EmptyToolSelectionMeansNone
            ? requested
            : GetDefaultToolKeys(traits);
    }
}
