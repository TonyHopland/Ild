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
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();

        var wi = ctx.WorkItem;
        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);
        if (run == null)
            return new NodeOutcome.Failed("LoopRun not found");

        string? prUrl = run.PrUrl;
        var repo = wi.RepositoryId != null ? await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value) : null;
        if (repo == null)
            return new NodeOutcome.Failed("Repository not found");
        var branch = run.BranchName ?? $"ild/wi-{wi.Id:N}";
        var target = repo.DefaultBranch ?? "main";

        // Commit + push on every visit so a re-entered PR node updates the
        // existing PR with the latest worktree state. The no-commits-ahead
        // guard only fires on initial PR creation; once a PR exists the
        // branch is allowed to be at parity with target (e.g. all changes
        // already merged or pushed previously).
        if (!string.IsNullOrEmpty(run.WorktreePath) && Directory.Exists(run.WorktreePath))
        {
            var repoManager = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();

            var diff = await repoManager.GetDiffAsync(run.WorktreePath);
            if (!string.IsNullOrEmpty(diff))
            {
                if (!await repoManager.CommitAsync(run.WorktreePath, wi.Title))
                    return new NodeOutcome.Failed("Failed to commit uncommitted changes");
            }

            var pushResult = await repoManager.PushAsync(run.WorktreePath, branch, ctx.CancellationToken);
            if (!pushResult.Success)
                return new NodeOutcome.Failed($"Failed to push branch '{branch}' to remote: {pushResult.Error ?? "unknown error"}");

            if (string.IsNullOrEmpty(prUrl))
            {
                _ = await repoManager.FetchAsync(run.WorktreePath, ctx.CancellationToken);
                var ahead = await repoManager.GetCommitsAheadCountAsync(run.WorktreePath, $"origin/{target}");
                if (ahead == 0)
                    return new NodeOutcome.Failed($"Branch '{branch}' has no commits ahead of 'origin/{target}'; no changes were made");
            }
        }

        if (string.IsNullOrEmpty(prUrl))
        {
            var remote = scope.ServiceProvider.GetRequiredService<IRemoteProvider>();

            var prBody = await ResolvePrDescriptionAsync(scope.ServiceProvider, ctx.Node.Config, ctx.Run.Id, wi, ctx.PreviousNodeOutput);

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

            run.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            await loopRunStore.UpdateRunAsync(run);
            prUrl = run.PrUrl;
        }

        // PR exists; suspend until the webhook signals merge or rejection.
        return new NodeOutcome.Suspended(
            "PR awaiting merge",
            SuspendKind.ExternalSignal,
            prUrl);
    }

    static async Task<string> ResolvePrDescriptionAsync(IServiceProvider sp, string? nodeConfig, Guid runId, WorkItemView wi, string? previousNodeOutput)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(nodeConfig);
        var template = cfg.PrDescriptionTemplate;

        if (string.IsNullOrEmpty(template))
            return wi.Description ?? "";

        // Prefer the DI-registered service so all rendering shares the same
        // event-log lookup and resolver instance. Fall back to a direct
        // construction so unit tests that don't wire the full service graph
        // (e.g. PRNodeExecutorTests) keep working: rendering without an
        // event log just produces an empty {{EventLog.*}} expansion.
        var rendering = sp.GetService<IPromptRenderingService>();
        if (rendering != null)
            return await rendering.RenderAsync(template, runId, wi, previousNodeOutput);

        var resolver = sp.GetService<IPromptTemplateResolver>() ?? new PromptTemplateResolver();
        return resolver.Render(template, new PromptContext(
            WorkItemTitle: wi.Title,
            WorkItemDescription: wi.Description,
            PreviousNodeOutput: previousNodeOutput));
    }
}
