using ILD.Data.DTOs;

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

    /// <summary>
    /// Build the environment variables the MCP server needs: the ILD API URL,
    /// an optional API token, and the current loop-run id. Returned as a
    /// loosely-typed dictionary so each adapter can splice it directly into its
    /// CLI config JSON.
    /// </summary>
    public static Dictionary<string, object?> BuildEnvironment(LoopRunContext? runContext)
    {
        var env = new Dictionary<string, object?>
        {
            ["ILD_API_URL"] = Environment.GetEnvironmentVariable("ILD_API_URL") ?? "http://localhost:5000",
        };

        var apiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN");
        if (!string.IsNullOrEmpty(apiToken))
            env["ILD_API_TOKEN"] = apiToken;

        if (runContext != null)
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
    public static string? ResolveServerDll()
    {
        var envOverride = Environment.GetEnvironmentVariable("ILD_MCP_SERVER_DLL");
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
}
