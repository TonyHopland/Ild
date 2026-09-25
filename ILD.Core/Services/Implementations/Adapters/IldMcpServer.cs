using System.Text.Json;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Shared locator and environment builder for the ILD MCP server that CLI
/// agents are wired to. Each adapter formats this into its own CLI-specific
/// config shape (Claude: <c>{command, args, env}</c>; opencode:
/// <c>{type, command, environment}</c>), but the DLL path and the environment
/// variables are identical across adapters and live here so the two never drift.
/// </summary>
public static class IldMcpServer
{
    private const string DllName = "ild-mcp-server.dll";
    private const string ConfigDirectorySegment = "ild-mcp-config";

    /// <summary>
    /// Write an MCP config file for an agent CLI to read and return its path, or
    /// <c>null</c> when it cannot be written, in which case the agent runs without
    /// its MCP servers and <paramref name="logger"/> gets a warning naming the path
    /// and the reason. The config carries the ILD API token,
    /// so it goes into <see cref="AgentIsolation.AgentReadRoot"/>: readable by the
    /// agent group, changeable by the orchestrator only. The caller deletes it once
    /// the CLI exits; its name carries <paramref name="loopRunId"/> (the session id
    /// for a chat turn) so <see cref="DeleteConfigFiles"/> can clear a run's files,
    /// and <see cref="AgentRunFiles.SweepAtStartupAsync(IReadOnlySet{Guid}, ILogger, CancellationToken)"/>
    /// clears the ones a dead process left.
    /// </summary>
    /// <param name="environment">Where the agent read root is configured; the process environment by default.</param>
    public static string? TryWriteConfigFile(
        string namePrefix, Guid loopRunId, object config, ILogger logger, IProcessEnvironment? environment = null)
    {
        environment ??= ProcessEnvironment.Current;
        var root = AgentReadRoot(environment);
        string? path = null;
        try
        {
            path = Path.Combine(
                AgentIsolation.CreateAgentReadDirectoryUnder(
                    root, environment.Get(AgentIsolation.AgentUserEnvVar), ConfigDirectorySegment),
                $"{namePrefix}-{loopRunId:N}-{Guid.NewGuid():N}.json");
            AgentIsolation.WriteAgentReadableFile(path, JsonSerializer.SerializeToUtf8Bytes(config));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Could not write the MCP config {Path}, so the agent runs without its MCP servers: {Reason}",
                path ?? Path.Combine(root, ConfigDirectorySegment), ex.Message);
            return null;
        }
    }

    /// <summary>Delete every MCP config file written for a loop run or chat session.</summary>
    /// <param name="environment">Where the agent read root is configured; the process environment by default.</param>
    public static void DeleteConfigFiles(Guid loopRunId, IProcessEnvironment? environment = null)
        => DeleteConfigFiles(AgentReadRoot(environment ?? ProcessEnvironment.Current), loopRunId);

    /// <inheritdoc cref="DeleteConfigFiles(Guid, IProcessEnvironment?)"/>
    internal static void DeleteConfigFiles(string agentReadRoot, Guid loopRunId)
    {
        var directory = Path.Combine(agentReadRoot, ConfigDirectorySegment);
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, $"*-{loopRunId:N}-*.json"))
            File.Delete(file);
    }

    /// <summary>
    /// The MCP config files written before <paramref name="writtenBeforeUtc"/>,
    /// this process's start: those of runs killed with an earlier process, for the
    /// startup sweep to delete. A file this process wrote belongs to a CLI that may
    /// still be reading it, so it is left for its caller to delete.
    /// </summary>
    internal static IEnumerable<string> StaleConfigFiles(string agentReadRoot, DateTime writtenBeforeUtc)
    {
        var directory = Path.Combine(agentReadRoot, ConfigDirectorySegment);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(file => File.GetLastWriteTimeUtc(file) < writtenBeforeUtc)
            : [];
    }

    /// <summary>
    /// Build the environment variables the MCP server needs: the ILD API URL,
    /// an optional API token, and the originating context id. When
    /// <paramref name="chatSessionId"/> is set the server is told the chat session
    /// id (so created work items are stamped with it); otherwise it is told the
    /// current loop-run id. Returned as a loosely-typed dictionary so each adapter
    /// can splice it directly into its CLI config JSON.
    /// </summary>
    /// <param name="environment">Where the URL and token come from; the process environment by default.</param>
    public static Dictionary<string, object?> BuildEnvironment(
        LoopRunContext? runContext, Guid? chatSessionId = null, IProcessEnvironment? environment = null)
    {
        environment ??= ProcessEnvironment.Current;
        var env = new Dictionary<string, object?>
        {
            ["ILD_API_URL"] = environment.Get("ILD_API_URL") ?? "http://localhost:5000",
        };

        var apiToken = environment.Get("ILD_API_TOKEN");
        if (!string.IsNullOrEmpty(apiToken))
            env["ILD_API_TOKEN"] = apiToken;

        if (chatSessionId is { } chatId)
            env["ILD_CHAT_SESSION_ID"] = chatId.ToString();
        else if (runContext != null)
            env["ILD_LOOP_RUN_ID"] = runContext.LoopRunId.ToString();

        return env;
    }

    /// <summary>
    /// Locate the published <c>ild-mcp-server.dll</c>. Probes in order:
    ///   1. <c>ILD_MCP_SERVER_DLL</c> env var (explicit override),
    ///   2. next to the currently executing assembly,
    ///   3. walk up from the executing assembly looking for a sibling
    ///      <c>ILD.McpServer/bin/{Debug|Release}/net*</c> directory (dev case).
    /// Returns <c>null</c> if nothing is found.
    /// </summary>
    /// <param name="environment">Where the override is read; the process environment by default.</param>
    public static string? ResolveServerDll(IProcessEnvironment? environment = null)
    {
        var envOverride = (environment ?? ProcessEnvironment.Current).Get("ILD_MCP_SERVER_DLL");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return Path.GetFullPath(envOverride);

        var baseDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(baseDir, DllName);
        if (File.Exists(sibling)) return Path.GetFullPath(sibling);

        // Walk upwards (max 8 levels) looking for an ILD.McpServer build output.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidateRoot = Path.Combine(dir.FullName, "ILD.McpServer", "bin");
            if (!Directory.Exists(candidateRoot)) continue;

            // Prefer Release over Debug if both exist.
            foreach (var flavor in new[] { "Release", "Debug" })
            {
                var flavorDir = Path.Combine(candidateRoot, flavor);
                if (!Directory.Exists(flavorDir)) continue;
                var hit = Directory.GetFiles(flavorDir, DllName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="AgentIsolation.AgentReadRoot"/>, read from <paramref name="environment"/>
    /// rather than from the process.
    /// </summary>
    private static string AgentReadRoot(IProcessEnvironment environment)
        => AgentIsolation.ResolveAgentReadRoot(
            environment.Get(AgentIsolation.AgentReadRootEnvVar), environment.Get(AgentIsolation.AgentUserEnvVar));
}
