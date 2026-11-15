using System.Reflection;
using ModelContextProtocol.Server;

namespace ILD.McpServer;

/// <summary>The tool classes an external-agent grant is served over HTTP: every one that does not need a run or a chat.</summary>
public static class ExternalAgentTools
{
    /// <summary>Parameters of served tools that name a run or a chat, which a grant has neither of.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> HiddenParameters { get; } =
        new Dictionary<string, IReadOnlyList<string>> { ["create_workitem"] = ["createdByLoopRunId"] };

    private static IEnumerable<Type> ToolClasses { get; } = typeof(ExternalAgentTools).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>What every grant is offered.</summary>
    public static IReadOnlyList<Type> ToolTypes { get; } = ToolClasses
        .Where(t => t.GetCustomAttribute<RunOrChatScopedAttribute>() is null && t.GetCustomAttribute<GrantControlAttribute>() is null)
        .ToList();

    /// <summary>What a grant with control of work is offered on top of <see cref="ToolTypes"/>.</summary>
    public static IReadOnlyList<Type> ControlToolTypes { get; } = ToolClasses
        .Where(t => t.GetCustomAttribute<GrantControlAttribute>() is not null)
        .ToList();

    /// <summary>What ILD's own run and chat agents get over stdio: everything except what only a grant may use.</summary>
    public static IReadOnlyList<Type> RunAndChatToolTypes { get; } = ToolClasses
        .Where(t => t.GetCustomAttribute<GrantControlAttribute>() is null)
        .ToList();

    /// <summary>The names of the tools in <see cref="ControlToolTypes"/>.</summary>
    public static IReadOnlySet<string> ControlToolNames { get; } = ControlToolTypes
        .SelectMany(t => t.GetMethods())
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);
}
