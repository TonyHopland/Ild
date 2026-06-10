namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Branch (and therefore worktree path) naming for a run. Each run gets its
/// own branch so that two runs of the same work item never share one — git
/// forbids two worktrees checked out on the same branch, and a shared branch
/// is what let a prior run's commits leak into the next run. See ADR-0008.
/// </summary>
/// <remarks>
/// The name is a single flat segment under <c>ild/</c> (<c>-run-</c>, not
/// <c>/run-</c>) on purpose: a nested <c>ild/wi-X/run-Y</c> would collide
/// (git ref dir/file conflict) with any legacy <c>ild/wi-X</c> branch left
/// over from the old per-work-item scheme.
/// </remarks>
internal static class RunWorktreeNaming
{
    public static string BranchFor(string workItemId, Guid runId)
        => $"ild/wi-{Sanitize(workItemId)}-run-{runId:N}";

    private static string Sanitize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "unknown";
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
