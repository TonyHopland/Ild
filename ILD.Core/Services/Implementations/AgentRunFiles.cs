using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Every file ILD writes for an agent that carries a secret — the ILD API token or
/// a package feed PAT — and the two moments each must go: when its run or chat is
/// reclaimed or deleted, and at startup, for what a killed process left behind.
/// The files are:
/// <list type="bullet">
///   <item>pi's ILD extension, <c>AgentReadRoot/ild-pi-ext/&lt;id&gt;/ild.ts</c>;</item>
///   <item>the MCP configs handed to Copilot and Claude Code, and the ILD MCP
///   settings and custom-server launch files handed to Codex,
///   <c>AgentReadRoot/ild-mcp-config/*-&lt;id&gt;-*.json</c>, which a turn also
///   deletes when its CLI exits;</item>
///   <item>the package feed npm configs, <c>AgentReadRoot/ild-package-feeds/*.npmrc</c>,
///   which their launch or preview also deletes when it ends; only a run's own
///   (<c>&lt;id&gt;-*.npmrc</c>) go with the run;</item>
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
        PackageFeedCredentialFiles.DeleteRunFiles(loopRunId);
        return DeleteAgentDirectoriesAsync(loopRunId, ct);
    }

    private static async Task<bool> DeleteAgentDirectoriesAsync(Guid loopRunId, CancellationToken ct)
    {
        var pi = await PiAdapter.DeleteRunFilesAsync(loopRunId, ct);
        var codex = await CodexAdapter.DeleteRunFilesAsync(loopRunId, ct);
        return pi && codex;
    }

    /// <summary>
    /// Clear what a killed process left behind. Run at startup, before any run can
    /// resume: MCP configs, package feed npm configs and pi extensions written
    /// before this process started — the extensions also only for runs and chats not in
    /// <paramref name="activeRunAndChatIds"/> — and every legacy
    /// <c>extensions/ild.ts</c>. Each is handled on its own: one that cannot be
    /// removed is logged and the sweep carries on, so it never leaves the rest, and
    /// their tokens, behind. Returns whether everything is gone.
    /// </summary>
    public static async Task<bool> SweepAtStartupAsync(
        IRunHost host, IReadOnlySet<Guid> activeRunAndChatIds, ILogger logger, CancellationToken ct = default)
        => await DeleteLeftoversAsync(host, await host.ListLeftoversAsync(activeRunAndChatIds, ct), listedAll: true, logger, ct);

    /// <inheritdoc cref="SweepAtStartupAsync(IRunHost, IReadOnlySet{Guid}, ILogger, CancellationToken)"/>
    internal static async Task<bool> SweepAtStartupAsync(
        IReadOnlySet<Guid> activeRunAndChatIds, string agentReadRoot, string scratchRoot, DateTime startedUtc, ILogger logger, CancellationToken ct)
    {
        var (leftovers, listedAll) = LocalRunHost.ListLeftovers(activeRunAndChatIds, agentReadRoot, scratchRoot, startedUtc, logger);
        return await DeleteLeftoversAsync(LocalRunHost.Shared, leftovers, listedAll, logger, ct);
    }

    private static async Task<bool> DeleteLeftoversAsync(
        IRunHost host, IReadOnlyList<RunLeftover> leftovers, bool listedAll, ILogger logger, CancellationToken ct)
    {
        var clean = listedAll;
        foreach (var leftover in leftovers.Where(l => l.Kind != LocalRunHost.LegacyPiExtensionsKind))
        {
            try
            {
                await host.DeleteLeftoverAsync(leftover, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {Path}, which carries a secret; sweeping the rest", leftover.Location);
                clean = false;
            }
        }

        foreach (var legacy in leftovers.Where(l => l.Kind == LocalRunHost.LegacyPiExtensionsKind))
        {
            try
            {
                if (await host.DeleteLeftoverAsync(legacy, ct))
                    return clean;
                logger.LogWarning("Could not remove every legacy pi ILD extension in the pi agent directories under {ScratchRoot}", legacy.Location);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(ex, "Could not sweep the legacy pi ILD extensions under {ScratchRoot}", legacy.Location);
            }
            return false;
        }
        return clean;
    }
}
