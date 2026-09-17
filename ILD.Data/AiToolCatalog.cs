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

    private static readonly IReadOnlyList<AiToolDefinition> DefaultTools =
    [
        new(Read, "Read", "Read files and inspect the workspace."),
        new(Write, "Write", "Edit and create files in the workspace."),
        new(Execute, "Execute", "Run shell commands in the workspace."),
        IldTool,
    ];

    private static readonly IReadOnlyList<AiToolDefinition> CopilotTools = [IldTool];

    public static IReadOnlyList<AiToolDefinition> GetSupportedToolsForProviderType(string? providerType)
        => NormalizeProviderType(providerType) switch
        {
            "opencode" or "pi" or "claude-code" => DefaultTools,
            "copilot" => CopilotTools,
            _ => Array.Empty<AiToolDefinition>(),
        };

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

        if (selectedToolKeys is null)
            return GetDefaultToolKeysForProviderType(providerType);

        var supportedKeys = new HashSet<string>(supportedTools.Select(tool => tool.Key), StringComparer.OrdinalIgnoreCase);
        var requested = selectedToolKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(supportedKeys.Contains)
            .ToArray();

        return requested.Length > 0 || EmptySelectionMeansNone(providerType)
            ? requested
            : GetDefaultToolKeysForProviderType(providerType);
    }

    /// <summary>
    /// Copilot's only tool is <c>ild</c>, so an explicit selection without it is
    /// the one way to turn ILD off. Every other provider keeps treating an empty
    /// or fully-filtered selection as its defaults, which is what their saved
    /// loop steps and chats were stored under.
    /// </summary>
    private static bool EmptySelectionMeansNone(string? providerType)
        => NormalizeProviderType(providerType) is "copilot";

    private static string? NormalizeProviderType(string? providerType)
        => providerType?.Trim().ToLowerInvariant();
}
