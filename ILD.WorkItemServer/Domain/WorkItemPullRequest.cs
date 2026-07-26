namespace ILD.WorkItemServer.Domain;

/// <summary>
/// One pull request opened against a work item. A PR is work-item state, not
/// run state: it touches the repository and outlives the throwaway worktree,
/// branch and loop run that produced it — and it has to survive an ILD instance
/// being reset, so the server holds it rather than the ILD-local database.
/// </summary>
/// <param name="Url">The PR's URL — the identity a work item's PRs are deduplicated on.</param>
/// <param name="LoopRunId">
/// The ILD loop run that opened it, recorded for provenance. Opaque here, like
/// <c>RepositoryId</c>: the run may be reclaimed by its ILD instance, or belong
/// to an instance this server never hears from again.
/// </param>
/// <param name="Merged">Whether the PR has been observed merged. Sticky once true.</param>
/// <param name="CreatedAt">
/// When the PR entered this work item's history. Supplied by the client (the
/// start of the run that opened it) so the ordering matches the runs' order
/// rather than the order the server happened to hear about them; the server
/// falls back to its own clock when it is not supplied.
/// </param>
public sealed record WorkItemPullRequest(
    string Url,
    Guid? LoopRunId,
    bool Merged,
    DateTime CreatedAt);
