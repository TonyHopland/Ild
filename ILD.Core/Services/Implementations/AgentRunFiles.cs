using ILD.Core.Services.Implementations.Adapters;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Every file ILD writes for an agent that carries the ILD API token, and the two
/// moments each must go: when its run or chat is reclaimed or deleted, and at
/// startup, for what a killed process left behind. The files are:
/// <list type="bullet">
///   <item>pi's ILD extension, <c>AgentReadRoot/ild-pi-ext/&lt;id&gt;/ild.ts</c>;</item>
///   <item>the MCP configs handed to Copilot and Claude Code,
///   <c>AgentReadRoot/ild-mcp-config/*-&lt;id&gt;-*.json</c>, which a turn also
///   deletes when its CLI exits;</item>
///   <item>the HTTP-calling <c>extensions/ild.ts</c> older builds left in pi's agent
///   directories in shared scratch, which the agent owns.</item>
/// </list>
/// </summary>
public static class AgentRunFiles
{
    /// <summary>
    /// Remove everything kept for a loop run or chat session (chat turns run under
    /// the session id). A token file in the agent read root that cannot be removed
    /// throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>,
    /// for the caller to keep its run or chat and retry; what the agent left in its
    /// own directories never fails the caller. Returns whether those are fully gone.
    /// </summary>
    public static Task<bool> DeleteAsync(Guid loopRunId, CancellationToken ct = default)
    {
        IldMcpServer.DeleteConfigFiles(loopRunId);
        return PiAdapter.DeleteRunFilesAsync(loopRunId, ct);
    }

    /// <summary>
    /// Clear what a killed process left behind. Run at startup, before any run can
    /// resume: MCP configs written before this process started, the pi extensions
    /// of runs and chats not in <paramref name="activeRunAndChatIds"/>, and every
    /// legacy <c>extensions/ild.ts</c>. Each is handled on its own: one that cannot
    /// be removed is logged and the sweep carries on, so it never leaves the rest,
    /// and their tokens, behind. Returns whether everything is gone.
    /// </summary>
    public static Task<bool> SweepAtStartupAsync(IReadOnlySet<Guid> activeRunAndChatIds, ILogger logger, CancellationToken ct = default)
        => SweepAtStartupAsync(
            activeRunAndChatIds,
            AgentIsolation.AgentReadRoot,
            AgentIsolation.ScratchRoot,
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            logger,
            ct);

    /// <inheritdoc cref="SweepAtStartupAsync(IReadOnlySet{Guid}, ILogger, CancellationToken)"/>
    internal static async Task<bool> SweepAtStartupAsync(
        IReadOnlySet<Guid> activeRunAndChatIds, string agentReadRoot, string scratchRoot, DateTime startedUtc, ILogger logger, CancellationToken ct)
    {
        var clean = DeleteEach(() => IldMcpServer.StaleConfigFiles(agentReadRoot, startedUtc), File.Delete, logger);
        clean &= DeleteEach(() => PiAdapter.StaleExtensions(agentReadRoot, activeRunAndChatIds), path => Directory.Delete(path, recursive: true), logger);

        try
        {
            if (await PiAdapter.SweepLegacyExtensionsAsync(scratchRoot, ct))
                return clean;
            logger.LogWarning("Could not remove every legacy pi ILD extension in the pi agent directories under {ScratchRoot}", scratchRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not sweep the legacy pi ILD extensions under {ScratchRoot}", scratchRoot);
        }
        return false;
    }

    private static bool DeleteEach(Func<IEnumerable<string>> list, Action<string> delete, ILogger logger)
    {
        List<string> paths;
        try
        {
            paths = list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list the token-bearing agent files a previous process left");
            return false;
        }

        var clean = true;
        foreach (var path in paths)
        {
            try
            {
                delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {Path}, which carries the ILD API token; sweeping the rest", path);
                clean = false;
            }
        }
        return clean;
    }
}
