using System.Reflection;
using ModelContextProtocol.Server;

namespace ILD.McpServer;

/// <summary>The tool classes an external-agent grant is served over HTTP: every one that does not need a run or a chat.</summary>
public static class ExternalAgentTools
{
    public static IReadOnlyList<Type> ToolTypes { get; } = typeof(ExternalAgentTools).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
                    && t.GetCustomAttribute<RunOrChatScopedAttribute>() is null)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();
}
