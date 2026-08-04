using ILD.Core.Services.Interfaces;
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
        var holder = await _runs.GetByBranchNameAsync(name);
        if (holder is not null)
        {
            var whose = string.Equals(holder.WorkItemId, workItemId, StringComparison.Ordinal)
                ? "this work item"
                : $"work item {holder.WorkItemId}";
            return Conflict(
                $"Branch `{name}` is already used locally by run {holder.Id} of {whose}. " +
                "Delete that run to free the branch, or give this work item a different branch name.");
        }

        if (repositoryId is null)
            return BranchNameVerdict.Usable;
        var repo = await _providers.GetRepositoryByIdAsync(repositoryId.Value);
        if (repo is null)
            return BranchNameVerdict.Usable;

        // The base clone may not be on disk yet (the Start node clones on
        // demand), in which case there are no local branches to collide with.
        var basePath = BaseRepoPath.Existing(repo, _config);
        if (basePath is not null && await _git.LocalBranchExistsAsync(basePath, name))
            return Conflict(
                $"Branch `{name}` already exists locally in the repository. " +
                "Delete it, or give this work item a different branch name.");

        var auth = await ResolveAuthAsync(repo);
        var onOrigin = await _git.RemoteHasBranchAsync(repo.CloneUrl, name, cancellationToken, auth);
        if (onOrigin == true)
            return Conflict(
                $"Branch `{name}` already exists on origin. " +
                "ILD never deletes remote branches — remove it yourself, or give this work item a different branch name.");
        if (onOrigin is null)
            _log?.LogWarning(
                "Could not ask origin whether branch '{Branch}' exists for repository {RepositoryId}; treating it as free",
                name, repo.Id);

        return BranchNameVerdict.Usable;
    }

    private static BranchNameVerdict Conflict(string message) => new(null, message);

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
