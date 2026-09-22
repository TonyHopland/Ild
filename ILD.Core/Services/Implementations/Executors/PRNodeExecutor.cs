using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PRNodeExecutor : INodeExecutor
{
    /// <summary>
    /// Work-item tag that opts a run into turning on auto-merge for the PR this
    /// node creates, when the repository supports it. Matched case-insensitively,
    /// like Condition node tag checks.
    /// </summary>
    private const string AutoMergeTag = "AutoMerge";

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
            // A signal with no text of its own (a human firing the edge from the
            // feedback UI) would hand the next node an empty
            // {{PreviousNode.Output}} — an agent wired to on_ci_failed would be
            // told nothing at all. Describe the edge from the last polled
            // snapshot instead, which is the same detail the heartbeat would
            // have sent had it got there first.
            var result = string.IsNullOrWhiteSpace(ctx.Run.ExternalActionResult)
                ? PrNodeEdges.Describe(ctx.Run.ExternalActionEdgeName, PrSnapshotJson.TryParse(ctx.Run.PrSnapshot),
                    workItemId: ctx.Run.WorkItemId)
                : ctx.Run.ExternalActionResult;
            yield return NodeOutcome.FromExternalAction(
                result, ctx.Run.ExternalActionResultType, ctx.Run.ExternalActionEdgeName, "PR rejected");
            yield break;
        }

        var rendering = sp.GetService<IPromptRenderingService>();
        string? renderedPrompt = cfg.Prompt;
        if (!string.IsNullOrEmpty(cfg.Prompt) && rendering is not null)
        {
            try { renderedPrompt = await rendering.RenderAsync(cfg.Prompt, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput); }
            catch { }
        }

        yield return new NodeOutcome.NodeStarting(renderedPrompt);

        var remoteProvider = await providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider is null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);
        var branch = ctx.Run.BranchName ?? RunWorktreeNaming.BranchFor(wi.Id, ctx.Run.Id);
        // The PR targets the branch the run was built from, not the repository
        // default: an item that continues or hotfixes a branch is asking for its
        // work to go back to that branch, and the "no commits ahead" guard below
        // is only meaningful against the ref the worktree was rebased onto.
        var target = RunBaseBranch.Resolve(ctx.Run, repo);
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
            // Before the call, so the window it opens cannot start after a
            // comment that was posted while the pull request was being created.
            var openedAt = DateTime.UtcNow;
            try { var r = await remote.CreatePullRequestAsync(repo.CloneUrl, branch, target, wi.Title, body); prResult = r; }
            catch (Exception ex) { err = ex.Message; }
            if (err is not null || prResult is null || !string.IsNullOrEmpty(prResult.Error))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"PR creation failed: {err ?? prResult?.Error ?? "unknown"}");
                yield break;
            }
            prUrl = prResult.HtmlUrl ?? prResult.Url ?? string.Empty;
            yield return new NodeOutcome.PrCreated(prUrl);

            // Start watching from the moment this pull request existed.
            //
            // Without this the ledger stays null until the node posts something,
            // and the first heartbeat then treats EVERYTHING already on the pull
            // request as history it has seen — so anything said between creating
            // the pull request and that first poll is never handed over. Normally
            // that is up to one heartbeat interval; if polls fail it is unbounded.
            // A run that predates this still arrives with no ledger and still
            // seeds from what is there, which is right: it did not open that
            // pull request and has no claim to have been watching it.
            if (sp.GetService<ILoopRunStore>() is { } opening)
            {
                await PrCommentLedgerWriter.MutateAsync(opening, ctx.Run.Id, state =>
                    state ?? PrCommentLedger.Empty with { WatchedFrom = openedAt });
            }

            // Opt-in auto-merge: when the work item carries the AutoMerge tag, ask
            // the provider to merge the PR once its checks pass. Best-effort — a
            // repository that does not support auto-merge leaves the PR open for
            // the heartbeat/human merge path, so a false result is not a failure.
            if (wi.Tags.Any(t => string.Equals(t, AutoMergeTag, StringComparison.OrdinalIgnoreCase)))
            {
                var prNumber = RemotePrUrl.ExtractPrNumber(prUrl);
                if (!string.IsNullOrEmpty(prNumber))
                    await remote.EnablePullRequestAutoMergeAsync(repo.CloneUrl, prNumber);
            }
        }
        else if (!string.IsNullOrEmpty(cfg.PrCommentTemplate) || await HasQueuedWritesAsync(ctx, sp))
        {
            // PR already exists for this run — render the comment template and
            // post it on the existing PR. Each re-visit of this node posts a
            // fresh comment. This is also where the round's queued replies and
            // resolutions go out: agents record what they intend to write and
            // nothing reaches the pull request until here, so a human has the
            // whole round to see it and drop any of it.
            var remote = sp.GetRequiredService<IRemoteProvider>();
            var prNumber = RemotePrUrl.ExtractPrNumber(prUrl);
            if (string.IsNullOrEmpty(prNumber))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"Cannot derive PR number from '{prUrl}' to post comment");
                yield break;
            }
            // Re-read rather than reuse the branch condition: an agent can queue
            // between the two, and this decides whether anything general is said.
            var answeredOnThreads = await HasQueuedWritesAsync(ctx, sp);
            if (string.IsNullOrEmpty(cfg.PrCommentTemplate) || answeredOnThreads)
            {
                // A round that answered on the threads has said everything it has
                // to say, where the objection was raised. The template comment on
                // top of that is a second notification carrying no information —
                // "Addressed the latest comments." under a set of replies that
                // already are the addressing.
                await DrainQueuedWritesAsync(ctx, sp, remote, repo.CloneUrl, prNumber);
                yield return new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, renderedPrompt ?? prUrl);
                yield break;
            }
            string commentBody = cfg.PrCommentTemplate;
            if (rendering is not null)
            {
                try { commentBody = await rendering.RenderAsync(cfg.PrCommentTemplate, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput); }
                catch { }
            }
            // Stamp it: unmarked, this comment is indistinguishable from a
            // reviewer's and fires on_comment on the next heartbeat — round,
            // comment, round, comment, each one a full verification gate.
            RemotePrWriteResult? posted = null;
            string? commentErr = null;
            try
            {
                posted = await remote.CreatePullRequestCommentAsync(
                    repo.CloneUrl, prNumber, PrCommentMarker.Stamp(commentBody, ctx.Run.Id));
            }
            catch (Exception ex) { commentErr = ex.Message; }
            if (posted is not { Ok: true })
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                    $"PR comment failed: {commentErr ?? posted?.Message ?? "remote returned false"}");
                yield break;
            }

            // The marker's second half, which no one can edit away. Optional
            // store: an id the provider did not name costs the ledger an entry,
            // not the round.
            if (posted.Id is not null && sp.GetService<ILoopRunStore>() is { } runs)
            {
                // Opening a ledger here rather than merging into one means the
                // run has never watched this pull request — nothing has read it.
                // Stamp that moment, so whoever watches first treats what was
                // already there as history instead of delivering all of it.
                await PrCommentLedgerWriter.MutateAsync(runs, ctx.Run.Id, state =>
                    (state ?? PrCommentLedger.Empty with { WatchedFrom = DateTime.UtcNow })
                        .WithPosted(PrCommentLedger.KeyFor("issue", posted.Id)));
            }

            await DrainQueuedWritesAsync(ctx, sp, remote, repo.CloneUrl, prNumber);
        }

        // Surface the rendered prompt template to the work item as the parked
        // node's content — mirrors the Human node, which parks on its rendered
        // prompt. Falls back to the PR URL when the node has no prompt template.
        yield return new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, renderedPrompt ?? prUrl);
    }

    /// <summary>Attempts before a contended claim gives up; each re-reads the row it lost to.</summary>
    private const int QueueClaimAttempts = 5;

    /// <summary>
    /// Whether anything is waiting to go out, read from the row rather than from
    /// the instance the engine has been carrying since the iteration began. An
    /// agent queues its answers DURING the round — after that instance was
    /// loaded — so the in-memory copy says "nothing waiting" for exactly the
    /// replies this node exists to send, and a node with no comment template
    /// would skip the branch that sends them.
    /// </summary>
    private static async Task<bool> HasQueuedWritesAsync(NodeExecutionContext ctx, IServiceProvider sp)
    {
        // With no store there is no row, and so no queue: the intents an agent
        // records live nowhere else.
        if (sp.GetService<ILoopRunStore>() is not { } runs)
            return false;
        return PrCommentQueueJson.TryParse(await runs.GetPrCommentQueueAsync(ctx.Run.Id)).Count > 0;
    }

    /// <summary>
    /// Claims the whole queue in one step — reads the column and clears it only
    /// if it still holds what was read, retrying when it does not — and hands
    /// back what it took.
    ///
    /// Claiming BEFORE anything goes out is what makes dropping a real stop. The
    /// instance the engine carries was loaded when the iteration began, which on
    /// a round that did any work at all is long before this; posting from it
    /// would send a list that no longer describes what the human wants said, and
    /// clearing the column afterwards would throw away an intent queued in
    /// between. A drop that lands before the claim changes what is claimed; one
    /// that lands after is late by definition, and the panel now says so.
    /// </summary>
    private static async Task<IReadOnlyList<PrQueuedWrite>> ClaimQueuedWritesAsync(
        ILoopRunStore runs, LoopRun run)
    {
        for (var attempt = 0; attempt < QueueClaimAttempts; attempt++)
        {
            var current = await runs.GetPrCommentQueueAsync(run.Id);
            var queued = PrCommentQueueJson.TryParse(current);
            if (queued.Count == 0)
                return queued;

            if (await runs.TrySetPrCommentQueueAsync(run.Id, current, null))
                return queued;
        }

        return Array.Empty<PrQueuedWrite>();
    }

    /// <summary>
    /// Write what the round said it intended to write. This is the only place a
    /// reply or a resolution reaches the pull request: the agent tools record
    /// them, a human can drop any of them up to this moment, and whatever is
    /// still queued goes out here — where the node's own comment goes out, and
    /// stamped the same way, so an answer never fires <c>on_comment</c> back at
    /// the loop.
    ///
    /// A refused write does not fail the node. Nothing is lost by it: the thread
    /// stays open and the finding stays undelivered, so the next review raises
    /// it again — whereas failing here would park the run on something no human
    /// asked for. The claim happens either way, so a provider that keeps
    /// refusing cannot make the node retry for ever.
    /// </summary>
    private static async Task DrainQueuedWritesAsync(
        NodeExecutionContext ctx, IServiceProvider sp, IRemoteProvider remote, string cloneUrl, string prNumber)
    {
        if (sp.GetService<ILoopRunStore>() is not { } runs)
            return;

        var queued = await ClaimQueuedWritesAsync(runs, ctx.Run);
        if (queued.Count == 0)
            return;

        // The claim emptied the queue, so the panel is now offering to stop
        // answers that are on their way out. Same signal the service sends when
        // an agent queues or a person drops: the column changed, re-read it.
        if (sp.GetService<IRunNotifier>() is { } notifier)
            await notifier.PrQueueChangedAsync(ctx.Run.Id);

        var log = sp.GetService<ILogger<PRNodeExecutor>>();
        var posted = new List<string>();

        // Findings whose answer never arrived. The poll path marked each one
        // delivered when it handed it over, so leaving them marked would mean a
        // refused answer quietly retires the finding: the thread stays open on
        // the pull request and nothing ever raises it again.
        var unanswered = new List<string>();

        foreach (var write in queued)
        {
            try
            {
                var stamped = PrCommentMarker.Stamp(write.Body ?? string.Empty, ctx.Run.Id);
                var result = write.Kind switch
                {
                    PrQueuedWrite.Resolve => await remote.ResolveReviewThreadAsync(cloneUrl, prNumber, write.TargetId),
                    // Nothing to reply on, so it goes out as a comment of its own.
                    PrQueuedWrite.Comment => await remote.CreatePullRequestCommentAsync(cloneUrl, prNumber, stamped),
                    _ => await remote.ReplyToReviewThreadAsync(cloneUrl, prNumber, write.TargetId, stamped),
                };

                if (!result.Ok)
                {
                    log?.LogWarning("Queued PR {Kind} on {Target} was refused: {Message}",
                        write.Kind, write.TargetId, result.Message);
                    if (write.SourceHash is { } refusedHash)
                        unanswered.Add(refusedHash);
                }
                else if (result.Id is not null && write.Kind != PrQueuedWrite.Resolve)
                {
                    // Keyed by the space it landed in: a thread reply is a review
                    // comment, a standalone answer is a pull-request comment.
                    posted.Add(PrCommentLedger.KeyFor(
                        write.Kind == PrQueuedWrite.Comment ? "issue" : "review", result.Id));
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Queued PR {Kind} on {Target} could not be written", write.Kind, write.TargetId);
                if (write.SourceHash is { } thrownHash)
                    unanswered.Add(thrownHash);
            }
        }

        if (posted.Count == 0 && unanswered.Count == 0)
            return;

        // Against the ledger as it stands: this node has been executing for the
        // length of a round, and the heartbeat has been writing this column
        // throughout it.
        await PrCommentLedgerWriter.MutateAsync(runs, ctx.Run.Id, state =>
        {
            var ledger = state ?? PrCommentLedger.Empty with { WatchedFrom = DateTime.UtcNow };
            foreach (var key in posted)
                ledger = ledger.WithPosted(key);
            foreach (var hash in unanswered)
                ledger = ledger.ForgetDeliveredContent(hash);
            return ledger;
        });
    }
}
