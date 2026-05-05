using System.Text.Json;
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

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(ctx.Node.Config);
        return JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), prompt = cfg.Prompt ?? "(no prompt)" });
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null)
            return new NodeOutcome.Failed("WorkItem not found");

        string? prUrl = wi.PrUrl;
        if (string.IsNullOrEmpty(prUrl))
        {
            var repo = await workItemStore.GetRepositoryAsync(wi.RepositoryId);
            if (repo == null)
                return new NodeOutcome.Failed("Repository not found");
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var target = repo.DefaultBranch ?? "main";

            if (!string.IsNullOrEmpty(wi.WorktreePath) && Directory.Exists(wi.WorktreePath))
            {
                var repoManager = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();

                var diff = await repoManager.GetDiffAsync(wi.WorktreePath);
                if (!string.IsNullOrEmpty(diff))
                {
                    if (!await repoManager.CommitAsync(wi.WorktreePath, wi.Title))
                        return new NodeOutcome.Failed("Failed to commit uncommitted changes");
                }

                var pushResult = await repoManager.PushAsync(wi.WorktreePath, branch, ctx.CancellationToken);
                if (!pushResult.Success)
                    return new NodeOutcome.Failed($"Failed to push branch '{branch}' to remote: {pushResult.Error ?? "unknown error"}");

                _ = await repoManager.FetchAsync(wi.WorktreePath, ctx.CancellationToken);
                var ahead = await repoManager.GetCommitsAheadCountAsync(wi.WorktreePath, $"origin/{target}");
                if (ahead == 0)
                    return new NodeOutcome.Failed($"Branch '{branch}' has no commits ahead of 'origin/{target}'; no changes were made");
            }

            var remote = scope.ServiceProvider.GetRequiredService<IRemoteProvider>();

            var prBody = ResolvePrDescription(_sp, ctx.Node.Config, wi, ctx.PreviousNodeOutput);

            var prResult = await remote.CreatePullRequestAsync(
                repo.CloneUrl,
                branch,
                target,
                wi.Title,
                prBody);

            if (prResult == null)
                return new NodeOutcome.Failed("PR creation returned no result");

            if (!string.IsNullOrEmpty(prResult.Error))
                return new NodeOutcome.Failed($"PR creation failed: {prResult.Error}");

            wi.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            await workItemStore.UpdateAsync(wi);
            prUrl = wi.PrUrl;
        }

        // PR exists; suspend until the webhook signals merge or rejection.
        return new NodeOutcome.Suspended(
            "PR awaiting merge",
            SuspendKind.ExternalSignal,
            prUrl);
    }

    static string ResolvePrDescription(IServiceProvider sp, string? nodeConfig, WorkItem wi, string? previousNodeOutput)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(nodeConfig);
        var template = cfg.PrDescriptionTemplate;

        if (string.IsNullOrEmpty(template))
            return wi.Description ?? "";

        var resolver = sp.GetService<IPromptTemplateResolver>() ?? new PromptTemplateResolver();
        return resolver.Render(template, new PromptContext(
            WorkItemTitle: wi.Title,
            WorkItemDescription: wi.Description,
            PreviousNodeOutput: previousNodeOutput));
    }
}
