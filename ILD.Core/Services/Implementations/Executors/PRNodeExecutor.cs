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
        var providerStore = sp.GetRequiredService<IProviderStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }
        if (wi.RepositoryId is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "PR node requires a repository on the work item");
            yield break;
        }
        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
        if (repo is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Repository not found");
            yield break;
        }

        // Re-entry path: signal arrived. Skip NodeStarting to avoid creating
        // a second LoopRunNode — the existing WaitingHuman node covers this visit.
        if (ctx.Run.ExternalActionResult is not null)
        {
            switch (ctx.Run.ExternalActionResultType)
            {
                case ExternalActionResultType.Reject:
                    yield return new NodeOutcome.Fail(EdgeType.OnFailure, "PR rejected", ctx.Run.ExternalActionResult);
                    yield break;
                case ExternalActionResultType.Respond:
                    yield return new NodeOutcome.Success(EdgeType.OnRespond, ctx.Run.ExternalActionResult);
                    yield break;
                default:
                    yield return new NodeOutcome.Success(EdgeType.OnSuccess, ctx.Run.ExternalActionResult);
                    yield break;
            }
        }

        var rendering = sp.GetService<IPromptRenderingService>();
        string? renderedPrompt = null;
        if (!string.IsNullOrEmpty(cfg.Prompt) && rendering is not null)
        {
            try { renderedPrompt = await rendering.RenderAsync(cfg.Prompt, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput); }
            catch { }
        }

        yield return new NodeOutcome.NodeStarting(renderedPrompt ?? cfg.Prompt);

        var remoteProvider = await providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider is null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);
        var branch = ctx.Run.BranchName ?? $"ild/wi-{wi.Id:N}";
        var target = repo.DefaultBranch ?? "main";
        var repoManager = sp.GetRequiredService<IRepositoryManager>();

        if (!string.IsNullOrEmpty(ctx.Run.WorktreePath) && Directory.Exists(ctx.Run.WorktreePath))
        {
            string? prepError = null;
            try
            {
                var diff = await repoManager.GetDiffAsync(ctx.Run.WorktreePath);
                if (!string.IsNullOrEmpty(diff)
                    && !await repoManager.CommitAsync(ctx.Run.WorktreePath, wi.Title))
                {
                    prepError = "Failed to commit uncommitted changes";
                }
                if (prepError is null)
                {
                    var pushResult = await repoManager.PushAsync(ctx.Run.WorktreePath, branch, ctx.CancellationToken, gitAuth);
                    if (!pushResult.Success)
                        prepError = $"Failed to push branch '{branch}': {pushResult.Error ?? "unknown error"}";
                }
                if (prepError is null && string.IsNullOrEmpty(ctx.Run.PrUrl))
                {
                    await repoManager.FetchAsync(ctx.Run.WorktreePath, ctx.CancellationToken, gitAuth);
                    var ahead = await repoManager.GetCommitsAheadCountAsync(ctx.Run.WorktreePath, $"origin/{target}");
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

        string? prUrl = ctx.Run.PrUrl;
        if (string.IsNullOrEmpty(prUrl))
        {
            var remote = sp.GetRequiredService<IRemoteProvider>();
            string body = wi.Description ?? string.Empty;
            if (!string.IsNullOrEmpty(cfg.PrDescriptionTemplate) && rendering is not null)
            {
                try { body = await rendering.RenderAsync(cfg.PrDescriptionTemplate, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput); }
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
            prUrl = prResult.HtmlUrl ?? prResult.Url ?? string.Empty;
            yield return new NodeOutcome.PrCreated(prUrl);
        }
        else if (!string.IsNullOrEmpty(cfg.PrCommentTemplate))
        {
            // PR already exists for this run — render the comment template and
            // post it on the existing PR. Each re-visit of this node posts a
            // fresh comment.
            var remote = sp.GetRequiredService<IRemoteProvider>();
            var prNumber = ExtractPrNumber(prUrl);
            if (string.IsNullOrEmpty(prNumber))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"Cannot derive PR number from '{prUrl}' to post comment");
                yield break;
            }
            string commentBody = cfg.PrCommentTemplate;
            if (rendering is not null)
            {
                try { commentBody = await rendering.RenderAsync(cfg.PrCommentTemplate, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput); }
                catch { }
            }
            bool posted = false;
            string? commentErr = null;
            try { posted = await remote.CreatePullRequestCommentAsync(repo.CloneUrl, prNumber, commentBody); }
            catch (Exception ex) { commentErr = ex.Message; }
            if (!posted)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"PR comment failed: {commentErr ?? "remote returned false"}");
                yield break;
            }
        }

        yield return new NodeOutcome.WaitingAction("PR awaiting merge", prUrl);
    }

    private static string? ExtractPrNumber(string prUrl)
    {
        if (string.IsNullOrEmpty(prUrl)) return null;
        // Both GitHub (.../pull/N) and Forgejo (.../pulls/N) end with the
        // numeric PR id as the last non-empty segment.
        var segments = prUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var seg = segments[i].TrimEnd('?', '#');
            if (seg.Length > 0 && seg.All(char.IsDigit)) return seg;
        }
        return null;
    }
}
