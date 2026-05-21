using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PRNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.PR;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(ctx.Node.Config);
        var sp = ctx.Services;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var providerStore = sp.GetRequiredService<IProviderStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);
        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (run is null || wi is null)
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Run or WorkItem not found");
            yield break;
        }
        if (wi.RepositoryId is null)
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "PR node requires a repository on the work item");
            yield break;
        }
        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
        if (repo is null)
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Repository not found");
            yield break;
        }

        // Re-entry path: signal arrived.
        if (run.ExternalActionResult is not null)
        {
            yield return new NodeOutcome.NodeStarting(run.PrUrl);
            if (run.ExternalActionResultRejected)
                yield return new NodeOutcome.Fail(EdgeType.OnReject, "PR rejected", run.ExternalActionResult);
            else
                yield return new NodeOutcome.Success(EdgeType.OnRespond, run.ExternalActionResult);
            yield break;
        }

        var rendering = sp.GetService<IPromptRenderingService>();
        string? renderedPrompt = null;
        if (!string.IsNullOrEmpty(cfg.Prompt) && rendering is not null)
        {
            try { renderedPrompt = await rendering.RenderAsync(cfg.Prompt, run.Id, wi, run.PreviousNodeOutput); }
            catch { }
        }

        yield return new NodeOutcome.NodeStarting(renderedPrompt ?? cfg.Prompt);

        var remoteProvider = await providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider is null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);
        var branch = run.BranchName ?? $"ild/wi-{wi.Id:N}";
        var target = repo.DefaultBranch ?? "main";
        var repoManager = sp.GetRequiredService<IRepositoryManager>();

        if (!string.IsNullOrEmpty(run.WorktreePath) && Directory.Exists(run.WorktreePath))
        {
            string? prepError = null;
            try
            {
                var diff = await repoManager.GetDiffAsync(run.WorktreePath);
                if (!string.IsNullOrEmpty(diff)
                    && !await repoManager.CommitAsync(run.WorktreePath, wi.Title))
                {
                    prepError = "Failed to commit uncommitted changes";
                }
                if (prepError is null)
                {
                    var pushResult = await repoManager.PushAsync(run.WorktreePath, branch, ctx.CancellationToken, gitAuth);
                    if (!pushResult.Success)
                        prepError = $"Failed to push branch '{branch}': {pushResult.Error ?? "unknown error"}";
                }
                if (prepError is null && string.IsNullOrEmpty(run.PrUrl))
                {
                    await repoManager.FetchAsync(run.WorktreePath, ctx.CancellationToken, gitAuth);
                    var ahead = await repoManager.GetCommitsAheadCountAsync(run.WorktreePath, $"origin/{target}");
                    if (ahead == 0)
                        prepError = $"Branch '{branch}' has no commits ahead of 'origin/{target}'";
                }
            }
            catch (Exception ex) { prepError = ex.Message; }
            if (prepError is not null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, prepError);
                yield break;
            }
        }

        string? prUrl = run.PrUrl;
        if (string.IsNullOrEmpty(prUrl))
        {
            var remote = sp.GetRequiredService<IRemoteProvider>();
            string body = wi.Description ?? string.Empty;
            if (!string.IsNullOrEmpty(cfg.PrDescriptionTemplate) && rendering is not null)
            {
                try { body = await rendering.RenderAsync(cfg.PrDescriptionTemplate, run.Id, wi, run.PreviousNodeOutput); }
                catch { }
            }
            RemotePrResult? prResult = null;
            string? err = null;
            try { var r = await remote.CreatePullRequestAsync(repo.CloneUrl, branch, target, wi.Title, body); prResult = r; }
            catch (Exception ex) { err = ex.Message; }
            if (err is not null || prResult is null || !string.IsNullOrEmpty(prResult.Error))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"PR creation failed: {err ?? prResult?.Error ?? "unknown"}");
                yield break;
            }
            run.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            prUrl = run.PrUrl;
            await loopRunStore.UpdateRunAsync(run);
        }

        yield return new NodeOutcome.WaitingAction("PR awaiting merge", prUrl);
    }
}
