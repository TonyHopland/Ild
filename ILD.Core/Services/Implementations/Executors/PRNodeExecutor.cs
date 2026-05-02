using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PRNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.PR;
    private readonly IServiceProvider _sp;

    public PRNodeExecutor(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null)
            return NodeExecutionResult.Fail("WorkItem not found");

        if (string.IsNullOrEmpty(wi.PrUrl))
        {
            var repo = await workItemStore.GetRepositoryAsync(wi.RepositoryId);
            if (repo == null)
                return NodeExecutionResult.Fail("Repository not found");
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var target = repo.DefaultBranch ?? "main";

            if (!string.IsNullOrEmpty(wi.WorktreePath) && Directory.Exists(wi.WorktreePath))
            {
                var repoManager = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();

                var diff = await repoManager.GetDiffAsync(wi.WorktreePath);
                if (!string.IsNullOrEmpty(diff))
                {
                    if (!await repoManager.CommitAsync(wi.WorktreePath, wi.Title))
                        return NodeExecutionResult.Fail("Failed to commit uncommitted changes");
                }

                if (!await repoManager.PushAsync(wi.WorktreePath, branch, ctx.CancellationToken))
                    return NodeExecutionResult.Fail($"Failed to push branch '{branch}' to remote");
            }

            var remote = scope.ServiceProvider.GetRequiredService<IRemoteProvider>();

            var prResult = await remote.CreatePullRequestAsync(
                repo.CloneUrl,
                branch,
                target,
                wi.Title,
                wi.Description ?? "");

            if (prResult == null)
                return NodeExecutionResult.Fail("PR creation returned no result");

            if (!string.IsNullOrEmpty(prResult.Error))
                return NodeExecutionResult.Fail($"PR creation failed: {prResult.Error}");

            wi.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            await workItemStore.UpdateAsync(wi);
        }

        return NodeExecutionResult.Ok(wi.PrUrl);
    }
}
