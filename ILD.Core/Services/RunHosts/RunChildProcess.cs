using System.Text.Json;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// A process the agent started and left running when its turn ended (a long test run, a build). <see cref="OutputPath"/> is the
/// regular file its standard output goes to, when it has one, so the tail can be read after it ends; null when it writes to a pipe or terminal.
/// </summary>
public sealed record RunChildProcess(int Pid, string CommandLine, string? OutputPath = null);

/// <summary>
/// How a launch's child processes are told from the launch itself and from the helpers that live on beyond it.
/// An agent launch is given <see cref="LaunchVariable"/> in its environment, which everything it starts inherits, so the processes
/// of one launch are found by that value even after the CLI exits and its children are reparented.
/// </summary>
public static class AgentChildren
{
    /// <summary>The environment variable naming the launch (a fresh id per launch) in every process the launch started.</summary>
    public const string LaunchVariable = "ILD_AGENT_LAUNCH";

    /// <summary>
    /// Parts of a command line that are never a child worth waiting for: ILD's own stdio MCP server, and build servers that stay up
    /// by design after a build (an MSBuild node, the Roslyn compiler server) and would otherwise keep a node waiting until the timeout.
    /// </summary>
    public static readonly IReadOnlyList<string> LongLivedHelpers =
    [
        "ild-mcp-server", "ILD.McpServer", "/nodemode:", "VBCSCompiler", "build-server", "MSBuildTaskHost",
    ];
}

/// <summary>What ILD remembers of the children a node is waiting for, kept on the run so a restart of ILD resumes the wait.</summary>
public sealed record ChildWaitState(DateTime StartedAt, IReadOnlyList<RunChildProcess> Processes)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The state in <paramref name="json"/>, or null when there is none or it cannot be read.</summary>
    public static ChildWaitState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ChildWaitState>(json, Json); }
        catch (JsonException) { return null; }
    }
}
