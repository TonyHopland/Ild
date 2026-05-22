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

        if (!hasHealthyWorktree)
        {
            var (ok, path, branch, error) = await EnsureWorktreeAsync(ctx, sp, repoManager, ctx.Run, repo, wi, gitAuth);
            if (!ok)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, error ?? "worktree setup failed");
                yield break;
            }
            yield return new NodeOutcome.WorktreeReady(path!, branch!);
        }
        else
        {
            yield return new NodeOutcome.WorktreeReady(
                ctx.Run.WorktreePath ?? string.Empty,
                ctx.Run.BranchName ?? string.Empty);
        }

        yield return new NodeOutcome.Success(EdgeType.OnSuccess, $"worktree={ctx.Run.WorktreePath}");
    }

    private static async Task<(bool Ok, string? Path, string? Branch, string? Error)> EnsureWorktreeAsync(
        NodeExecutionContext ctx, IServiceProvider sp, IRepositoryManager repoManager,
        LoopRun run, Repository repo, WorkItemView wi, GitAuthOptions? gitAuth)
    {
        var branch = run.BranchName ?? $"ild/wi-{wi.Id:N}";
        var basePath = repo.WorktreesPath;
        var cloned = false;
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(Path.Combine(basePath, ".git")))
        {
            var config = sp.GetService<IConfiguration>();
            var dataPath = config?["App:DataPath"];
            basePath = Path.GetFullPath(Path.Combine(
                string.IsNullOrWhiteSpace(dataPath) ? "data" : dataPath,
                "repos", repo.Id.ToString("N")));
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            if (!Directory.Exists(Path.Combine(basePath, ".git")))
            {
                var result = await repoManager.CloneAsync(repo.CloneUrl, basePath, ctx.CancellationToken, gitAuth);
                if (!result.Success) return (false, null, null, $"git clone failed: {result.Error}");
                cloned = true;
            }
        }
        var defaultBranch = repo.DefaultBranch ?? "main";
        if (!cloned)
        {
            try
            {
                await repoManager.FetchAsync(basePath, ctx.CancellationToken, gitAuth);
                await repoManager.ResetHardAsync(basePath, $"origin/{defaultBranch}", ctx.CancellationToken);
            }
            catch { /* best-effort */ }
        }
        var path = await repoManager.CreateWorktreeAsync(basePath, branch);
        await repoManager.FetchAsync(path, ctx.CancellationToken, gitAuth);
        try
        {
            var rebaseOk = await repoManager.RebaseAsync(path, $"origin/{defaultBranch}", ctx.CancellationToken);
            if (!rebaseOk) return (false, null, null, $"rebase onto origin/{defaultBranch} failed — worktree may be stale");
        }
        catch (Exception ex)
        {
            return (false, null, null, $"rebase onto origin/{defaultBranch} failed: {ex.Message}");
        }
        return (true, path, branch, null);
    }
}
