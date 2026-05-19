using ILD.Data.DTOs;

namespace ILD.Data;

public static class AiToolCatalog
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Execute = "execute";
    public const string Ild = "ild";

    private static readonly IReadOnlyList<AiToolDefinition> DefaultTools =
    [
        new(Read, "Read", "Read files and inspect the workspace."),
        new(Write, "Write", "Edit and create files in the workspace."),
        new(Execute, "Execute", "Run shell commands in the workspace."),
        new(Ild, "Ild", "Use ILD-specific tools such as work item and loop APIs."),
    ];

    public static IReadOnlyList<AiToolDefinition> GetSupportedToolsForProviderType(string? providerType)
        => IsDefaultAgentProvider(providerType) ? DefaultTools : Array.Empty<AiToolDefinition>();

    public static IReadOnlyList<string> GetDefaultToolKeysForProviderType(string? providerType)
        => GetSupportedToolsForProviderType(providerType)
            .Where(tool => tool.DefaultEnabled)
            .Select(tool => tool.Key)
            .ToArray();

    public static IReadOnlyList<string> NormalizeSelectedToolKeys(string? providerType, IEnumerable<string?>? selectedToolKeys)
    {
        var supportedTools = GetSupportedToolsForProviderType(providerType);
        if (supportedTools.Count == 0)
            return Array.Empty<string>();

        var supportedKeys = new HashSet<string>(supportedTools.Select(tool => tool.Key), StringComparer.OrdinalIgnoreCase);
        var requested = selectedToolKeys?
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(supportedKeys.Contains)
            .ToArray();

        return requested is { Length: > 0 }
            ? requested
            : GetDefaultToolKeysForProviderType(providerType);
    }

    private static bool IsDefaultAgentProvider(string? providerType)
        => providerType?.Trim().ToLowerInvariant() is "opencode" or "pi";
}
