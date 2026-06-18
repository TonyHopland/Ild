using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Composes several work items' current run branches into one throwaway
/// integration worktree off <c>main</c> and runs the existing worktree preview
/// on it. The integration branch is test-only and never delivered — each item
/// still merges its own PR. See the "Preview together" work item and ADR-0008.
/// </summary>
public interface ICombinedPreviewService
{
    /// <summary>
    /// Build (or rebuild) the integration worktree for <paramref name="request"/>'s
    /// members and, if the merge composes cleanly, start its preview. On the first
    /// merge conflict the result reports the colliding member/files instead.
    /// </summary>
    Task<CombinedPreviewResponse> StartAsync(CombinedPreviewStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current state of the combined preview for <paramref name="workItemIds"/>:
    /// the running preview if one is live (with a staleness flag when a member has
    /// re-run), otherwise a plan showing the integration branch and member branches.
    /// </summary>
    Task<CombinedPreviewResponse> GetAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Continue a combined preview that halted on a conflict resolved in the
    /// worktree: finish the merge commit, merge any members the conflict skipped
    /// past, and start the preview. Reports the conflict again if markers remain.
    /// </summary>
    Task<CombinedPreviewResponse> ResumeAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the combined preview and tear down its integration worktree and branch.
    /// Member branches and their PRs are never touched.
    /// </summary>
    Task<CombinedPreviewResponse> StopAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default);
}
