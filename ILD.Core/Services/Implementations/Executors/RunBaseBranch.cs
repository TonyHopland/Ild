using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// The ref a run is built from: what the Start node fetches, resets the base
/// repo to, and rebases the run's worktree onto, and what the PR node targets.
/// One answer for both — a PR that opened against a different branch than the
/// run was rebased onto would report a diff nobody asked for.
/// </summary>
/// <remarks>
/// The work item's <c>BaseBranchOverride</c> is pinned onto the run at creation
/// (see <c>LoopEngine.StartRunAsync</c>), so this reads the run and never the
/// work item: an edit mid-run must not redirect a run already under way. A null
/// pin means the repository's default branch, which is what every run used
/// before overrides existed, so runs predating the column resolve unchanged.
/// This only chooses <em>which</em> clean ref a run starts from; ADR-0006's
/// requirement that it be clean, and that failing to reach it fails the node,
/// is unaffected.
/// </remarks>
internal static class RunBaseBranch
{
    public static string Resolve(LoopRun run, Repository repo)
        => Trimmed(run.BaseBranchOverride) ?? Trimmed(repo.DefaultBranch) ?? "main";

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
