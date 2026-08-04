namespace ILD.Core.Services.Interfaces;

/// <summary>
/// What a work item's custom branch name is worth right now: whether it is a
/// legal branch name at all, and — if it is — whether anything already holds it.
/// The two are kept apart because they are answered at different moments and
/// carry different weight. A <see cref="ValidationError"/> is a property of the
/// name itself, so it blocks the save that introduced it. A
/// <see cref="Conflict"/> is a property of the world, which moves between the
/// save and the run, so at save time it is only advice; the binding answer is
/// the one taken at run start.
/// </summary>
public sealed record BranchNameVerdict(string? ValidationError, string? Conflict)
{
    /// <summary>A usable name: legal, and free for the next run to claim.</summary>
    public static readonly BranchNameVerdict Usable = new(null, null);

    /// <summary>The single reason this name cannot be used, or null when it can.</summary>
    public string? Problem => ValidationError ?? Conflict;

    public bool IsUsable => Problem is null;
}

/// <summary>
/// Vets a work item's <c>BranchNameOverride</c>. Used by the API to refuse an
/// illegal name outright and to warn — without blocking — about one that is
/// already taken, and by the engine to refuse to start a run on a branch that
/// already exists. That last check is what keeps ADR-0006's clean-base
/// invariant intact under custom names: a run never starts on a pre-existing
/// branch, so there is never a prior run's work underneath it.
/// </summary>
public interface IBranchNameOverrideService
{
    /// <summary>
    /// Whether <paramref name="branchName"/> can be the branch of the next run
    /// of <paramref name="workItemId"/> in <paramref name="repositoryId"/>.
    /// Free means: no run row holds it, the repository's base clone has no such
    /// branch, and <c>origin</c> does not publish one. A remote that cannot be
    /// reached is not treated as a conflict — the Start node fails the run on an
    /// unreachable origin anyway, and refusing to start over an unanswered
    /// question would park work items over a flaky network.
    /// </summary>
    /// <param name="workItemId">
    /// The item the name is being vetted for, so a conflict can say whether the
    /// run holding it is this item's own. Null/empty when the item does not
    /// exist yet (create-time advice).
    /// </param>
    Task<BranchNameVerdict> InspectAsync(
        string? branchName,
        Guid? repositoryId,
        string? workItemId,
        CancellationToken cancellationToken = default);
}
