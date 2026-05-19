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
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(ctx.Node.Config);
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();

        var wi = ctx.WorkItem;
        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);
        if (run == null)
            return new NodeOutcome.Failed("LoopRun not found");

        // Surface the resolved prompt via the event log so the UI can
        // display exactly what the human is being asked. We deliberately
        // do NOT stash it on the run-node Output: that slot belongs to
        // the human's eventual answer.
        string? renderedPrompt = null;
        if (!string.IsNullOrEmpty(cfg.Prompt))
        {
            var rendering = scope.ServiceProvider.GetService<IPromptRenderingService>();
            if (rendering != null)
            {
                renderedPrompt = await rendering.RenderAsync(cfg.Prompt, ctx.Run.Id, wi, ctx.PreviousNodeOutput);
                if (!string.IsNullOrEmpty(renderedPrompt))
                {
                    var eventLog = scope.ServiceProvider.GetService<IEventLogService>();
                    if (eventLog != null)
                    {
                        try
                        {
                            await eventLog.AppendAsync(
                                ctx.Run.Id,
                                PrPromptRenderedEvent,
                                renderedPrompt,
                                ctx.Node.Id,
                                runNodeId: ctx.RunNode.Id);
                        }
                        catch { /* best-effort observability */ }
                    }
                }
            }
        }

        string? prUrl = run.PrUrl;
        var repo = wi.RepositoryId != null ? await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value) : null;
        if (repo == null)
            return new NodeOutcome.Failed("Repository not found");
        var remoteProvider = await providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider == null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);
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

            var pushResult = await repoManager.PushAsync(run.WorktreePath, branch, ctx.CancellationToken, gitAuth);
            if (!pushResult.Success)
                return new NodeOutcome.Failed($"Failed to push branch '{branch}' to remote: {pushResult.Error ?? "unknown error"}");

            if (string.IsNullOrEmpty(prUrl))
            {
                _ = await repoManager.FetchAsync(run.WorktreePath, ctx.CancellationToken, gitAuth);
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
        else
        {
            // PR already exists — add a comment using the configured template (if any).
            await TryAddPrCommentAsync(scope.ServiceProvider, ctx.Node.Config, ctx.Run.Id, wi, ctx.PreviousNodeOutput, repo, prUrl);
        }

        // PR exists; suspend until the webhook signals merge or rejection.
        return new NodeOutcome.Suspended(
            "PR awaiting merge",
            SuspendKind.ExternalSignal,
            prUrl,
            renderedPrompt);
    }

    static async Task<string> ResolvePrDescriptionAsync(IServiceProvider sp, string? nodeConfig, Guid runId, WorkItemView wi, string? previousNodeOutput)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(nodeConfig);
        var template = cfg.PrDescriptionTemplate;

        if (string.IsNullOrEmpty(template))
            return wi.Description ?? "";

        return await RenderTemplateAsync(sp, template, runId, wi, previousNodeOutput);
    }

    /// <summary>
    /// Shared rendering pipeline for PR templates. Tries the DI-registered
    /// <see cref="IPromptRenderingService"/> first (which has event-log access),
    /// then falls back to a bare <see cref="IPromptTemplateResolver"/> so unit
    /// tests that don't wire the full service graph still work.
    /// </summary>
    static async Task<string> RenderTemplateAsync(IServiceProvider sp, string template, Guid runId, WorkItemView wi, string? previousNodeOutput)
    {
        var rendering = sp.GetService<IPromptRenderingService>();
        if (rendering != null)
            return await rendering.RenderAsync(template, runId, wi, previousNodeOutput);

        var resolver = sp.GetService<IPromptTemplateResolver>() ?? new PromptTemplateResolver();
        return resolver.Render(template, new PromptContext(
            WorkItemTitle: wi.Title,
            WorkItemDescription: wi.Description,
            PreviousNodeOutput: previousNodeOutput));
    }

    static async Task TryAddPrCommentAsync(IServiceProvider sp, string? nodeConfig, Guid runId, WorkItemView wi, string? previousNodeOutput, Repository repo, string prUrl)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(nodeConfig);
        if (string.IsNullOrEmpty(cfg.PrCommentTemplate))
            return;

        var commentBody = await RenderTemplateAsync(sp, cfg.PrCommentTemplate, runId, wi, previousNodeOutput);
        if (string.IsNullOrEmpty(commentBody))
            return;

        var remote = sp.GetRequiredService<IRemoteProvider>();
        var prNumber = ExtractPrNumber(prUrl);
        if (prNumber == null)
            return;

        _ = await remote.CreatePullRequestCommentAsync(repo.CloneUrl, prNumber, commentBody);
    }

    /// <summary>
    /// Extract the PR number from a PR URL. Works for both Forgejo
    /// (<c>/pulls/{n}</c>) and GitHub (<c>/pull/{n}</c>) URL schemes.
    /// </summary>
    static string? ExtractPrNumber(string prUrl)
    {
        // Try "/pulls/{number}" (Forgejo) first, then "/pull/{number}" (GitHub)
        foreach (var segment in new[] { "/pulls/", "/pull/" })
        {
            var idx = prUrl.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var remainder = prUrl[(idx + segment.Length)..];
                // Take everything up to the next slash or query string
                var trimmed = remainder.Split('/', '?')[0];
                if (int.TryParse(trimmed, out _))
                    return trimmed;
            }
        }
        return null;
    }

    public const string PrPromptRenderedEvent = "PrPromptRendered";
}
