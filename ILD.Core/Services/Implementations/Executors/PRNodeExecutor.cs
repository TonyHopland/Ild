using ILD.Data;
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
        // Read the pinned repository straight off the run already in hand; the
        // work item's is only a fallback for a run that has no pinned repository.
        var repositoryId = ctx.Run.RepositoryId ?? wi.RepositoryId;
        if (repositoryId is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "PR node requires a repository on the work item");
            yield break;
        }
        var repo = await providerStore.GetRepositoryByIdAsync(repositoryId.Value);
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

        var hasWorktree = !string.IsNullOrEmpty(ctx.Run.WorktreePath) && Directory.Exists(ctx.Run.WorktreePath);
        // Read now: the engine records the pull request on this same run while
        // the visit that gets it is still going.
        var hadPullRequest = !string.IsNullOrEmpty(ctx.Run.PrUrl);
        var pushed = false;
        // Where origin had the branch before this visit pushed, when it could be read.
        string? originBeforePush = null;
        if (hasWorktree)
        {
            string? prepError = null;
            try
            {
                var diff = await repoManager.GetDiffAsync(ctx.Run.WorktreePath!);
                if (!string.IsNullOrEmpty(diff)
                    && !await repoManager.CommitAsync(ctx.Run.WorktreePath!, wi.Title))
                {
                    prepError = "Failed to commit uncommitted changes";
                }
                if (prepError is null)
                {
                    var integration = await IntegrateWithOriginAsync(ctx, repoManager, branch, gitAuth);
                    prepError = integration.Error;
                    originBeforePush = integration.OriginHead;
                    if (prepError is null && integration.Push)
                    {
                        var pushResult = await repoManager.PushAsync(ctx.Run.WorktreePath!, branch, ctx.CancellationToken, gitAuth);
                        if (!pushResult.Success)
                            prepError = $"Failed to push branch '{branch}': {pushResult.Error ?? "unknown error"}";
                        pushed = pushResult.Success;
                    }
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
            // The visit where the run gets its pull request: the branch's open
            // one if it has one, a new one if not, as the node's toggles allow.
            // Later visits never come here, so the toggles and on_existing_pr
            // apply exactly once per run.
            var remote = sp.GetRequiredService<IRemoteProvider>();
            var lookedUpAt = DateTime.UtcNow;
            var existing = await remote.FindOpenPullRequestAsync(repo.CloneUrl, branch, target, ctx.CancellationToken);
            if (existing.Error is not null)
            {
                // Never read as "there is none": creating on a failed lookup is
                // exactly the duplicate pull request the lookup is there to avoid.
                yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                    $"Could not look up an open PR for branch `{branch}`: {existing.Error}");
                yield break;
            }

            if (existing.Number is { } number)
            {
                if (!cfg.MayAttach)
                {
                    yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                        $"PR #{number} already exists for branch `{branch}`; this node does not attach to existing PRs");
                    yield break;
                }

                prUrl = existing.HtmlUrl ?? existing.Url ?? string.Empty;

                // The run did not open this pull request, so what is already on
                // it is history, not news: watch it from now on. No auto-merge
                // either — it is not this run's pull request to merge unasked.
                if (sp.GetService<ILoopRunStore>() is { } attaching)
                {
                    await PrCommentLedgerWriter.MutateAsync(attaching, ctx.Run.Id, state =>
                        state ?? PrCommentLedger.Empty with { WatchedFrom = lookedUpAt });
                }

                var attachedNumber = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (await HasQueuedWritesAsync(ctx, sp))
                    await DrainQueuedWritesAsync(ctx, sp, remote, repo.CloneUrl, attachedNumber);

                if (await SeeAsync(remote, repoManager, ctx, pushed, repo.CloneUrl, attachedNumber) is { } attachedSeen)
                    yield return attachedSeen;

                // Recorded last, right before the node routes on: a run that
                // stops anywhere above comes back with no pull request, so it
                // looks up and attaches again rather than resuming as a later
                // visit that would never take on_existing_pr.
                yield return new NodeOutcome.PrAttached(prUrl, existing.CreatedAt);

                if (await IsOnExistingPrWiredAsync(ctx, sp))
                {
                    yield return new NodeOutcome.Success(EdgeType.Custom,
                        $"Attached to existing PR #{attachedNumber}: {prUrl}", LoopOutputs.OnExistingPr);
                    yield break;
                }
            }
            else
            {
                if (!cfg.MayCreate)
                {
                    yield return new NodeOutcome.Fail(EdgeType.OnFailure,
                        $"No open PR for branch `{branch}`; this node does not create PRs");
                    yield break;
                }

                // Only a pull request this node is about to open needs commits
                // of its own; one that already exists has nothing to guard.
                if (hasWorktree)
                {
                    string? guardError = null;
                    try
                    {
                        await repoManager.FetchAsync(ctx.Run.WorktreePath!, ctx.CancellationToken, gitAuth);
                        var ahead = await repoManager.GetCommitsAheadCountAsync(ctx.Run.WorktreePath!, $"origin/{target}");
                        if (ahead == 0)
                            guardError = $"Branch '{branch}' has no commits ahead of 'origin/{target}'";
                    }
                    catch (Exception ex) { guardError = ex.Message; }
                    if (guardError is not null)
                    {
                        yield return new NodeOutcome.Fail(EdgeType.OnFailure, guardError);
                        yield break;
                    }
                }

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
                // Back-dated by the skew allowance: this stamp is ILD's clock and
                // what it is compared against is the forge's.
                var openedAt = DateTime.UtcNow - PrCommentLedger.ClockSkewAllowance;
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

                if (RemotePrUrl.ExtractPrNumber(prUrl) is { Length: > 0 } createdNumber
                    && await SeeAsync(remote, repoManager, ctx, pushed, repo.CloneUrl, createdNumber) is { } createdSeen)
                    yield return createdSeen;

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
        }
        else if (await HasQueuedWritesAsync(ctx, sp))
        {
            // PR already exists for this run, and the round has things to say on
            // it. This is where they go out: agents record what they intend to
            // write and nothing reaches the pull request until here, so a human
            // has the whole round to see it and drop any of it. The node posts
            // nothing of its own: only the round knows whether it has anything
            // general to say, so it queues a comment when it does.
            var remote = sp.GetRequiredService<IRemoteProvider>();
            var prNumber = RemotePrUrl.ExtractPrNumber(prUrl);
            if (string.IsNullOrEmpty(prNumber))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"Cannot derive PR number from '{prUrl}' to send the round's answers");
                yield break;
            }

            await DrainQueuedWritesAsync(ctx, sp, remote, repo.CloneUrl, prNumber);
        }

        // A push of the run's own moves the head the heartbeat compares
        // against; recorded as the commit pushed — what the worktree has, not
        // what the forge may not have caught up to — it is not reported back
        // to the run as new commits. Only when origin was still at the head
        // the run had already seen: then the push added nothing but the run's
        // own commits. Otherwise someone else's commits are under it — rebased
        // onto here, on an earlier attempt, or pulled in by the round — which
        // the run has not been told of, so nothing is recorded and the next
        // park reports new commits. The visit that got the pull request has
        // already recorded what it found, everything already on it included.
        // With no head seen yet there is nothing to compare: the heartbeat's
        // first look reports nothing either way.
        var seenBefore = PrSnapshotJson.TryParse(ctx.Run.PrSnapshot)?.HeadSha;
        if (pushed && hadPullRequest
            && (seenBefore is null || string.Equals(originBeforePush, seenBefore, StringComparison.Ordinal))
            && await repoManager.ResolveCommitAsync(ctx.Run.WorktreePath!, "HEAD") is { } pushedHead)
            yield return new NodeOutcome.PrSeen(pushedHead);

        // Surface the rendered prompt template to the work item as the parked
        // node's content — mirrors the Human node, which parks on its rendered
        // prompt. Falls back to the PR URL when the node has no prompt template.
        yield return new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, renderedPrompt ?? prUrl);
    }

    /// <summary>
    /// The pull request as the visit that got it found it. Best-effort: a forge
    /// that does not answer leaves the stored snapshot as it was, which costs
    /// at most one <c>on_new_commits</c> the run did not need. When this visit
    /// pushed, the head recorded is the commit it pushed, not what the forge
    /// may not have caught up to yet.
    /// </summary>
    private static async Task<NodeOutcome.PrSeen?> SeeAsync(
        IRemoteProvider remote, IRepositoryManager repoManager, NodeExecutionContext ctx, bool pushed,
        string cloneUrl, string prNumber)
    {
        if (await remote.GetPullRequestSnapshotAsync(cloneUrl, prNumber, ctx.CancellationToken) is not { } snapshot)
            return null;
        var head = pushed ? await repoManager.ResolveCommitAsync(ctx.Run.WorktreePath!, "HEAD") : null;
        return new NodeOutcome.PrSeen(head ?? snapshot.HeadSha, snapshot);
    }

    /// <summary>
    /// Bring the worktree up to what origin has of the branch before pushing,
    /// and say whether there is anything of the run's own to push. Someone
    /// else can push to the branch while a round runs — always on a branch the
    /// run attached to, and on its own once that author is told of new
    /// commits — so origin's copy can be ahead of the worktree:
    /// <list type="bullet">
    ///   <item>the run has commits of its own as well: they are rebased onto
    ///         origin's copy when origin moved past the head the run last saw,
    ///         and a rebase that cannot complete fails the node saying so — a
    ///         plain push would be refused anyway;</item>
    ///   <item>the run has nothing of its own: nothing is pushed, and the round
    ///         goes on to send its queued writes.</item>
    /// </list>
    /// A run's own branch that origin does not have yet is pushed as before,
    /// as it is when origin cannot be asked: the push itself says whether it
    /// went through. A branch the run attached to is held to more: commits of
    /// the run's own go to it only once origin has been read, and one that is
    /// gone is never pushed again — that would recreate someone else's deleted
    /// branch.
    /// </summary>
    private static async Task<Integration> IntegrateWithOriginAsync(
        NodeExecutionContext ctx, IRepositoryManager repoManager, string branch, GitAuthOptions? gitAuth)
    {
        var worktree = ctx.Run.WorktreePath!;
        var attached = ctx.Run.AttachToExistingBranch;
        var upstream = $"origin/{branch}";
        if (!await repoManager.FetchAsync(worktree, ctx.CancellationToken, gitAuth))
        {
            if (!attached)
                return new Integration(true);
            // Nothing of the run's own even on the copy fetched last: there is
            // nothing to push, and the round's writes still go out.
            return await repoManager.GetCommitsAheadCountAsync(worktree, upstream) > 0
                ? new Integration(false, $"Could not fetch origin to check branch `{branch}` before pushing the run's commits to it; nothing was pushed")
                : new Integration(false);
        }

        if (!await repoManager.RemoteBranchExistsAsync(worktree, branch))
            return attached
                ? new Integration(false, $"Branch `{branch}` no longer exists on origin; this run attached to it and will not recreate it")
                : new Integration(true);

        var originHead = await repoManager.ResolveCommitAsync(worktree, upstream);
        var ahead = await repoManager.GetCommitsAheadCountAsync(worktree, upstream);
        var behind = await repoManager.GetCommitsBehindCountAsync(worktree, upstream);
        // A run's own branch is pushed as it always was unless there is plainly
        // nothing of its own to push; a branch it attached to only with
        // commits of its own on it. Either way, behind with nothing ahead is
        // nothing to push.
        if (behind == 0)
            return new Integration(!attached || ahead > 0, OriginHead: originHead);
        if (ahead == 0)
            return new Integration(false, OriginHead: originHead);

        // Ahead and behind: rebase only onto what someone else pushed — origin
        // past the head the run last saw. A run that rewrote its own pushed
        // history (rebased onto its base, say) is behind its own old commits,
        // and replaying them would push copies of the base; that push is left
        // to be refused, as it always was.
        var seen = PrSnapshotJson.TryParse(ctx.Run.PrSnapshot)?.HeadSha;
        var pushedByOthers = seen is null ? attached : !string.Equals(originHead, seen, StringComparison.Ordinal);
        if (pushedByOthers)
        {
            var rebase = await repoManager.RebaseAsync(worktree, upstream, ctx.CancellationToken);
            if (!rebase.Success)
            {
                var where = rebase.ConflictedFiles.Count > 0
                    ? $" (conflicts in {string.Join(", ", rebase.ConflictedFiles)})"
                    : rebase.Error is { Length: > 0 } error ? $": {error}" : string.Empty;
                return new Integration(false,
                    $"Branch `{branch}` moved on origin while this round ran, and the run's commits do not rebase onto it{where}; nothing was pushed");
            }
        }

        return new Integration(true, OriginHead: originHead);
    }

    /// <summary>
    /// What <see cref="IntegrateWithOriginAsync"/> found: whether to push, why
    /// not when the node must fail, and the commit origin had the branch at
    /// before anything was pushed, when origin could be read.
    /// </summary>
    private sealed record Integration(bool Push, string? Error = null, string? OriginHead = null);

    /// <summary>
    /// Whether this node has an edge from <c>on_existing_pr</c>. With no store
    /// there are no edges to read, so the node parks as it would unwired.
    /// </summary>
    private static async Task<bool> IsOnExistingPrWiredAsync(NodeExecutionContext ctx, IServiceProvider sp)
    {
        if (sp.GetService<ILoopRunStore>() is not { } runs)
            return false;
        var edges = await runs.GetEdgesForNodeIdsAsync(new[] { ctx.Node.Id });
        return edges.Any(e => e.SourceNodeId == ctx.Node.Id
            && e.EdgeType == EdgeType.Custom
            && string.Equals(e.Name, LoopOutputs.OnExistingPr, StringComparison.Ordinal));
    }

    /// <summary>Attempts before a contended claim gives up; each re-reads the row it lost to.</summary>
    private const int QueueClaimAttempts = 5;

    /// <summary>
    /// Whether anything is waiting to go out, read from the row rather than from
    /// the instance the engine has been carrying since the iteration began. An
    /// agent queues its answers DURING the round — after that instance was
    /// loaded — so the in-memory copy says "nothing waiting" for exactly the
    /// replies this node exists to send, and the node would skip the branch
    /// that sends them entirely.
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
    ///
    /// The claim is therefore irreversible, and that is the trade, not an
    /// oversight (D25). A crash between here and the writes loses those intents
    /// while their findings stay marked delivered: the round's answers are gone
    /// and the round looks finished. Holding them in an in-flight state instead
    /// would trade that for a DOUBLE-POSTED answer — a crash after the forge
    /// accepted a reply but before the entry cleared reposts it on restart — and
    /// a comment on a public pull request cannot be un-posted, while a lost
    /// intent is recoverable both by a human reading the queue and by the next
    /// review raising the finding again.
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
    /// What a claimed intent is called in the record: the handle it was aimed
    /// at, where that is in the diff, and enough of what it said to recognise.
    /// The id comes first and always — it is the only part a person can act on
    /// afterwards, and a general comment has no place in the diff at all.
    /// </summary>
    private static string Describe(PrQueuedWrite write)
    {
        var target = string.IsNullOrEmpty(write.TargetId) ? "the pull request" : write.TargetId;
        var where = write.Path is null ? string.Empty : $" ({write.Path}:{write.Line?.ToString() ?? "?"})";
        var said = string.IsNullOrWhiteSpace(write.Body)
            ? string.Empty
            : ": " + (write.Body!.Length <= ClaimRecordBodyLength ? write.Body : write.Body[..ClaimRecordBodyLength] + "…");
        return $"{write.Kind} on {target}{where}{said}";
    }

    /// <summary>Enough of an answer to recognise it in the event log, and no more.</summary>
    private const int ClaimRecordBodyLength = 200;

    /// <summary>
    /// Write down what the claim took. Best-effort, and deliberately so: a store
    /// that will not take the record must not stop the answers going out, which
    /// is the thing the round actually owes the pull request.
    /// </summary>
    private static async Task RecordClaimAsync(
        NodeExecutionContext ctx, IServiceProvider sp, IReadOnlyList<PrQueuedWrite> queued)
    {
        if (sp.GetService<IEventLogService>() is not { } events)
            return;

        try
        {
            await events.AppendAsync(ctx.Run.Id, EventType.PrQueuedWritesClaimed,
                string.Join("\n", queued.Select(Describe)));
        }
        catch (Exception ex)
        {
            sp.GetService<ILogger<PRNodeExecutor>>()?.LogWarning(
                ex, "Could not record the PR write claim for run {RunId}", ctx.Run.Id);
        }
    }

    /// <summary>
    /// Write what the round said it intended to write. This is the ONLY place
    /// anything reaches the pull request: the agent tools record intents, a
    /// human can drop any of them up to this moment, and whatever is still
    /// queued goes out here — every piece of it stamped, so nothing the round
    /// writes fires <c>on_comment</c> back at the loop. That includes the
    /// round's own general comment, which it queues like any other write; the
    /// node has no comment of its own to post.
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

        // Say what was taken, before a line of it goes out. The claim cannot be
        // undone, so this is the only thing standing between "the process died
        // here" and answers that are simply gone — the queue is empty, the round
        // reads as finished, and nothing else records that they ever existed.
        await RecordClaimAsync(ctx, sp, queued);

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
                var stamped = write.Kind == PrQueuedWrite.Finding
                    ? PrCommentMarker.Stamp(PrFinding.Headed(write.Severity, write.Body ?? string.Empty), ctx.Run.Id, write.FindingId, write.Severity)
                    : PrCommentMarker.Stamp(write.Body ?? string.Empty, ctx.Run.Id);
                var result = write.Kind switch
                {
                    PrQueuedWrite.Resolve => await remote.ResolveReviewThreadAsync(cloneUrl, prNumber, write.TargetId),
                    // A new review comment of the round's own, on the line it is about.
                    PrQueuedWrite.Finding => await remote.CreateReviewCommentAsync(
                        cloneUrl, prNumber, write.Path ?? string.Empty, write.Line ?? 1, stamped, ctx.CancellationToken),
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
