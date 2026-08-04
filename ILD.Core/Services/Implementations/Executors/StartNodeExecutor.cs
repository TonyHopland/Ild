using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class StartNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Start;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var sp = ctx.Services;
        var providerStore = sp.GetRequiredService<IProviderStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var repoManager = sp.GetRequiredService<IRepositoryManager>();

        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null || wi.RepositoryId is null)
        {
            yield return new NodeOutcome.NodeStarting("{\"nodeType\":\"Start\"}");
            yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                "WorkItem has no repository attached; refusing to run loop without an isolated worktree.");
            yield break;
        }

        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
        if (repo is null)
        {
            yield return new NodeOutcome.NodeStarting("{\"nodeType\":\"Start\"}");
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Repository not found");
            yield break;
        }

        var remoteProvider = await providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider is null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);

        yield return new NodeOutcome.NodeStarting(
            System.Text.Json.JsonSerializer.Serialize(new { nodeType = "Start", message = "initialized" }));

        var hasHealthyWorktree = !string.IsNullOrEmpty(ctx.Run.WorktreePath)
            && Directory.Exists(ctx.Run.WorktreePath)
            && await repoManager.ValidateWorktreeHealthAsync(ctx.Run.WorktreePath);

        string worktreePath;
        string branchName;
        if (!hasHealthyWorktree)
        {
            var (ok, path, branch, error) = await EnsureWorktreeAsync(ctx, sp, repoManager, ctx.Run, repo, wi, gitAuth);
            if (!ok)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, error ?? "worktree setup failed");
                yield break;
            }
            worktreePath = path!;
            branchName = branch!;
            yield return new NodeOutcome.WorktreeReady(worktreePath, branchName);
        }
        else
        {
            worktreePath = ctx.Run.WorktreePath ?? string.Empty;
            branchName = ctx.Run.BranchName ?? string.Empty;
            yield return new NodeOutcome.WorktreeReady(worktreePath, branchName);
        }

        var config = NodeConfig.Parse<NodeConfig.Start>(ctx.Node.Config);
        string? installWarning = null;
        if (config.RunInstall == true)
        {
            var preview = sp.GetService<IWorktreePreviewService>();
            if (preview is null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                    "install requested but the worktree preview service is unavailable.");
                yield break;
            }

            var (installError, warning) = await RunInstallAsync(preview, worktreePath, repo.PreviewEnv, ctx.CancellationToken);
            if (installError is not null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"ild.config install failed: {installError}");
                yield break;
            }
            installWarning = warning;
        }

        var successOutput = installWarning is null
            ? $"worktree={worktreePath}"
            : $"worktree={worktreePath}; warning: {installWarning}";
        yield return new NodeOutcome.Success(EdgeType.OnSuccess, successOutput);
    }

    private static async Task<(string? Error, string? Warning)> RunInstallAsync(
        IWorktreePreviewService preview, string worktreePath, string? customEnv, CancellationToken ct)
    {
        try
        {
            var result = await preview.InstallAsync(worktreePath, customEnv: customEnv, cancellationToken: ct);
            // A missing ild.config.json is expected for most projects — surface a
            // warning rather than failing the run on it.
            return result.Installed
                ? (null, null)
                : (null, $"ild.config install skipped: {result.Message}");
        }
        catch (Exception ex)
        {
            return (ex.Message, null);
        }
    }

    private static async Task<(bool Ok, string? Path, string? Branch, string? Error)> EnsureWorktreeAsync(
        NodeExecutionContext ctx, IServiceProvider sp, IRepositoryManager repoManager,
        LoopRun run, Repository repo, WorkItemView wi, GitAuthOptions? gitAuth)
    {
        var branch = run.BranchName ?? RunWorktreeNaming.BranchFor(wi.Id, run.Id);
        var basePath = repo.WorktreesPath;
        var cloned = false;
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(Path.Combine(basePath, ".git")))
        {
            basePath = BaseRepoPath.Fallback(repo.Id, sp.GetService<IConfiguration>());
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            if (!Directory.Exists(Path.Combine(basePath, ".git")))
            {
                var result = await repoManager.CloneAsync(repo.CloneUrl, basePath, ctx.CancellationToken, gitAuth);
                if (!result.Success) return (false, null, null, $"git clone failed: {result.Error}");
                cloned = true;
            }
        }
        var baseBranch = RunBaseBranch.Resolve(run, repo);
        if (!cloned)
        {
            var fetchOk = await repoManager.FetchAsync(basePath, ctx.CancellationToken, gitAuth);
            if (!fetchOk)
                return (false, null, null, $"failed to fetch origin for base repo — refusing to start run from a stale origin/{baseBranch}");
        }
        // A base the human typed can simply not be there. Say so once, plainly,
        // instead of letting it surface as a reset or rebase failure — and never
        // fall back to the default branch, which would silently build the run on
        // top of the wrong history. The repository's own default branch is
        // discovered from the remote rather than typed, so only an override is
        // worth the round trip.
        if (run.BaseBranchOverride is not null
            && !await repoManager.RemoteBranchExistsAsync(basePath, baseBranch))
        {
            return (false, null, null,
                $"base branch 'origin/{baseBranch}' does not exist on the remote — "
                + "fix the work item's base branch or push it, then re-run");
        }
        if (!cloned)
        {
            var resetOk = await repoManager.ResetHardAsync(basePath, $"origin/{baseBranch}", ctx.CancellationToken);
            if (!resetOk)
                return (false, null, null, $"failed to reset base repo to origin/{baseBranch}");
        }
        var path = await repoManager.CreateWorktreeAsync(basePath, branch);
        await repoManager.FetchAsync(path, ctx.CancellationToken, gitAuth);
        try
        {
            var rebase = await repoManager.RebaseAsync(path, $"origin/{baseBranch}", ctx.CancellationToken);
            if (!rebase.Success)
                return (false, null, null, $"rebase onto origin/{baseBranch} failed — worktree may be stale: {rebase.Error ?? "unknown error"}");
        }
        catch (Exception ex)
        {
            return (false, null, null, $"rebase onto origin/{baseBranch} failed: {ex.Message}");
        }
        return (true, path, branch, null);
    }
}
