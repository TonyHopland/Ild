using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="IBranchNameOverrideService"/>
public sealed class BranchNameOverrideService : IBranchNameOverrideService
{
    private readonly ILoopRunStore _runs;
    private readonly IProviderStore _providers;
    private readonly IRepositoryManager _git;
    private readonly IConfiguration? _config;
    private readonly ILogger<BranchNameOverrideService>? _log;

    public BranchNameOverrideService(
        ILoopRunStore runs,
        IProviderStore providers,
        IRepositoryManager git,
        IConfiguration? config = null,
        ILogger<BranchNameOverrideService>? log = null)
    {
        _runs = runs;
        _providers = providers;
        _git = git;
        _config = config;
        _log = log;
    }

    public async Task<BranchNameVerdict> InspectAsync(
        string? branchName,
        Guid? repositoryId,
        string? workItemId,
        bool attachToExistingBranch = false,
        CancellationToken cancellationToken = default)
    {
        var name = BranchNameRules.Normalize(branchName);
        if (name is null)
            return new BranchNameVerdict("Branch name cannot be empty.", null);

        var validationError = BranchNameRules.Validate(name);
        if (validationError is not null)
            return new BranchNameVerdict(validationError, null);

        // A run row is the strongest local claim and the most useful one to
        // report: it names who is holding the branch and how to let go of it.
        //
        // Deliberately global rather than scoped to the repository: every run's
        // worktree lands under one shared root keyed by branch name alone
        // (RepositoryManager.CreateWorktreeAsync), so two repositories cannot
        // both hold `feature/foo`. Should the worktree root ever become
        // per-repository, this must be narrowed to the repository or it starts
        // reporting conflicts that are not there.
        string? conflict = null;
        var holder = await _runs.GetByBranchNameAsync(name);
        if (holder is not null)
        {
            var whose = string.Equals(holder.WorkItemId, workItemId, StringComparison.Ordinal)
                ? "this work item"
                : $"work item {holder.WorkItemId}";
            // Neither way of freeing the branch will accept a run that is still
            // going, so an active holder is told to end it first rather than
            // sent to an action that would refuse it.
            var howToFree = holder.Status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman
                ? "That run is still active — let it finish or cancel it first, then clean it up (which keeps its history) or delete it"
                : "Clean up that run to free the branch while keeping its history, or delete it outright";
            conflict =
                $"Branch `{name}` is already used locally by run {holder.Id} of {whose}. " +
                $"{howToFree}, or give this work item a different branch name.";
        }

        var repo = repositoryId is null ? null : await _providers.GetRepositoryByIdAsync(repositoryId.Value);
        if (repo is null)
            return new BranchNameVerdict(null, conflict);

        // The base clone may not be on disk yet (the Start node clones on
        // demand), in which case there are no local branches to collide with.
        var basePath = BaseRepoPath.Existing(repo, _config);
        if (conflict is null && basePath is not null && await _git.LocalBranchExistsAsync(basePath, name))
            conflict =
                $"Branch `{name}` already exists locally in the repository. " +
                "Delete it, or give this work item a different branch name.";

        // Asked even when the name is already in conflict: the dialog offers
        // attaching off this answer, whatever else is holding the name.
        var auth = await ResolveAuthAsync(repo);
        var onOrigin = await _git.RemoteHasBranchAsync(repo.CloneUrl, name, cancellationToken, auth);
        if (onOrigin is null)
        {
            _log?.LogWarning(
                "Could not ask origin whether branch '{Branch}' exists for repository {RepositoryId}",
                name, repo.Id);
            // A fresh branch loses nothing to the unanswered question — the
            // Start node fails the run on the same unreachable origin — but an
            // attach needs the answer: without it nothing confirms there is a
            // branch to attach to.
            if (conflict is null && attachToExistingBranch)
                conflict = $"Could not reach origin to confirm that branch `{name}` exists, so there is nothing confirmed to attach to. " +
                    "Try again once origin answers.";
        }
        else if (conflict is null && onOrigin.Value != attachToExistingBranch)
            conflict = attachToExistingBranch
                ? $"Branch `{name}` does not exist on origin, so there is nothing to attach to. " +
                  "Push it, or clear Attach to existing branch."
                : $"Branch `{name}` already exists on origin. " +
                  "ILD never deletes remote branches — remove it yourself, give this work item a different branch name, " +
                  "or tick Attach to existing branch to work on it as it is.";

        return new BranchNameVerdict(null, conflict, onOrigin);
    }

    private async Task<GitAuthOptions?> ResolveAuthAsync(ILD.Data.Entities.Repository repo)
    {
        try
        {
            var provider = await _providers.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
            return provider is null ? null : new GitAuthOptions(repo.CloneUrl, provider.ApiKey, provider.Type);
        }
        catch (Exception ex)
        {
            // Without credentials a private remote does not answer, so the
            // upstream half of the check goes unanswered and — like any
            // unreachable origin — is not counted as a conflict, letting the run
            // proceed to the Start node, which fails it on the same fetch.
            _log?.LogDebug(ex, "Could not resolve git credentials for repository {RepositoryId}", repo.Id);
            return null;
        }
    }
}
