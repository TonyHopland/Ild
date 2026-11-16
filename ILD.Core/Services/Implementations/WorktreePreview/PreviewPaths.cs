using System.Text;
using ILD.Core.Services.RunHosts.Local;

namespace ILD.Core.Services.Implementations;

/// <summary>Where a worktree's preview keeps its state and logs.</summary>
public static class PreviewPaths
{
    /// <summary>
    /// The <c>${STATE_DIR}</c> a preview of <paramref name="worktreePath"/> uses on ILD's
    /// own machine, in whatever form the path is given, without creating it.
    /// </summary>
    public static string StateDirectoryOf(string worktreePath)
        => Path.Combine([AgentIsolation.ScratchRoot, .. StateDirectorySegments(Normalize(worktreePath))]);

    internal static string Normalize(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            throw new InvalidOperationException("Preview requires a worktree path.");

        return Path.GetFullPath(worktreePath);
    }

    private static string[] StateDirectorySegments(string normalizedWorktreePath)
        => ["preview", WorktreeSlug(normalizedWorktreePath)];

    /// <summary>
    /// Where the per-service and install logs go. Deliberately <em>not</em> the
    /// shared state directory: these files are opened, appended to and read back by
    /// the orchestrator — the stdout/stderr pumps
    /// (<see cref="PreviewLauncher.LaunchServiceAsync"/>, <see cref="PreviewLauncher.RunInstallStepsAsync"/>)
    /// run in-process, and <see cref="WorktreePreviewService.GetServiceLogAsync"/> serves them to
    /// <c>get_preview_logs</c>.
    ///
    /// <para>
    /// The path is a hash of the worktree, which the agent knows, and a directory it
    /// could write would let it pre-create <c>install.log</c> as a symlink to somewhere only the
    /// orchestrator can write — <c>~/.profile</c>, say, which the next
    /// <c>/bin/sh -lc</c> preview spawn would then execute as the orchestrator. The
    /// read side is the mirror: an arbitrary orchestrator-readable file, served
    /// through the API. The <c>0700</c> private root is what forecloses both; it
    /// is the same guarantee the git askpass helper relies on.
    /// </para>
    /// </summary>
    internal static string BuildLogDirectory(string worktreePath)
        => AgentIsolation.CreatePrivateDirectory("preview", WorktreeSlug(worktreePath));

    public static string WorktreeSlug(string worktreePath)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(worktreePath))).ToLowerInvariant();

    internal static string NpmCacheOf(string stateDirectory) => Path.Combine(stateDirectory, "npm-cache");
}
