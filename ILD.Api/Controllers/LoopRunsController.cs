using System.Text.Json;
using ILD.Api.Contracts;
using ILD.Api.Services;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class LoopRunsController : ControllerBase
{
    private readonly ILoopEngine _loopEngine;
    private readonly IEventLogService _eventLogService;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IAdapterSessionSnapshotStore _sessionSnapshotStore;
    private readonly InteractiveShellSessionService _shellSessions;
    private readonly IRunReclaimer _runReclaimer;
    private readonly IWorkItemManager _workItemManager;
    private readonly IPackageFeedResolver _packageFeeds;
    private readonly IRunConversationService _conversation;
    private readonly ILoopTemplateStore _templates;
    private readonly IGlobalVariableStore _globals;
    private readonly ISchedulerSettingsService _settings;
    private readonly ClaudeProjectFolders _claudeProjects;

    public LoopRunsController(
        ILoopEngine loopEngine,
        IEventLogService eventLogService,
        ILoopRunStore loopRunStore,
        IAdapterSessionSnapshotStore sessionSnapshotStore,
        InteractiveShellSessionService shellSessions,
        IRunReclaimer runReclaimer,
        IWorkItemManager workItemManager,
        IPackageFeedResolver packageFeeds,
        IRunConversationService conversation,
        ILoopTemplateStore templates,
        IGlobalVariableStore globals,
        ISchedulerSettingsService settings,
        ClaudeProjectFolders? claudeProjects = null)
    {
        _loopEngine = loopEngine;
        _eventLogService = eventLogService;
        _loopRunStore = loopRunStore;
        _sessionSnapshotStore = sessionSnapshotStore;
        _shellSessions = shellSessions;
        _runReclaimer = runReclaimer;
        _workItemManager = workItemManager;
        _packageFeeds = packageFeeds;
        _conversation = conversation;
        _templates = templates;
        _globals = globals;
        _settings = settings;
        _claudeProjects = claudeProjects ?? ClaudeProjectFolders.ForProcess();
    }

    /// <summary>
    /// Whether the run still owns a worktree or local branch to reclaim. The
    /// answer rather than the paths: it is all the cleanup affordance needs,
    /// and a run's worktree path is an absolute server path.
    /// </summary>
    private static bool HasLocalGitState(ILD.Data.Entities.LoopRun run)
        => !string.IsNullOrEmpty(run.WorktreePath) || !string.IsNullOrEmpty(run.BranchName);

    /// <summary>
    /// Until when the run's history is kept: when it ended plus the retention days
    /// in force now. None for a run that has not ended, or is pinned and so kept
    /// until it is unpinned. The retention sweeper also waits for the work item
    /// to be Done, which the run view says alongside.
    /// </summary>
    private async Task<DateTime?> HistoryKeptUntilAsync(ILD.Data.Entities.LoopRun run)
    {
        if (run.Retain || !RunCleanup.HasEnded(run) || run.CompletedAt is not { } endedAt)
            return null;
        return endedAt.AddDays(await _settings.GetRunRetentionDaysAsync());
    }

    /// <summary>
    /// The global variables still missing or empty, as they are now, for a run
    /// parked because its current node uses them; empty for every other run.
    /// </summary>
    private static async Task<IReadOnlyList<string>> MissingGlobalVariablesAsync(ILD.Data.Entities.LoopRun run, ParkReads reads)
    {
        if (run.HaltReason != HaltReason.GlobalVariableMissing || run.CurrentNodeId is not { } nodeId) return [];
        var node = (await reads.NodesAsync(run.LoopTemplateVersionId)).FirstOrDefault(n => n.Id == nodeId);
        if (node is null) return [];
        var names = NodePromptTemplates.GlobalVariableNames(node, run);
        return names.Count == 0 ? [] : NodePromptTemplates.Missing(names, await reads.GlobalsAsync());
    }

    /// <summary>
    /// What one request reads for its parked runs, each at most once: a version's
    /// nodes, shared by every run on that version, and the global variables.
    /// Awaited one run at a time, so the request's context never runs two queries at once.
    /// </summary>
    private sealed class ParkReads(ILoopTemplateStore templates, IGlobalVariableStore globals)
    {
        private readonly Dictionary<Guid, Task<IReadOnlyList<ILD.Data.Entities.LoopNode>>> _nodes = new();
        private Task<IReadOnlyList<ILD.Data.Entities.GlobalVariable>>? _globals;

        public Task<IReadOnlyList<ILD.Data.Entities.LoopNode>> NodesAsync(Guid versionId)
            => _nodes.TryGetValue(versionId, out var read) ? read : _nodes[versionId] = templates.GetNodesForVersionAsync(versionId);

        public Task<IReadOnlyList<ILD.Data.Entities.GlobalVariable>> GlobalsAsync() => _globals ??= globals.GetAllAsync();
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var runs = await _loopRunStore.GetAllAsync(skip, take);
        var reads = new ParkReads(_templates, _globals);
        var missingGlobals = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var r in runs)
            missingGlobals[r.Id] = await MissingGlobalVariablesAsync(r, reads);
        var result = runs.Select(r =>
        {
            var totals = RunCostTotals.From(r.RunNodes);
            return new
            {
                id = r.Id,
                workItemId = r.WorkItemId,
                loopTemplateId = r.LoopTemplateVersion?.LoopTemplateId,
                templateVersion = r.LoopTemplateVersion?.VersionNumber ?? 0,
                status = r.Status.ToString(),
                currentNodeId = r.CurrentNodeId,
                isPaused = r.IsPaused,
                isHalted = r.IsHalted,
                // Who parked it, so the UI can say so instead of inferring it
                // from a node's prose. Null means a human pressed Halt.
                haltReason = r.HaltReason?.ToString(),
                missingGlobalVariables = missingGlobals[r.Id],
                retain = r.Retain,
                hasLocalGitState = HasLocalGitState(r),
                nodeExecutionCount = r.NodeExecutionCount,
                aiTraversalCount = r.AiTraversalCount,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
                totalInputTokens = totals.TotalInputTokens,
                totalOutputTokens = totals.TotalOutputTokens,
                totalCostUsd = totals.TotalCostUsd,
                nodes = r.RunNodes.OrderBy(rn => rn.CreatedAt).Select(LoopRunNodeResponse.From).ToList(),
            };
        }).ToList();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null)
            return NotFound(new { error = "Run not found" });

        // Eager-load the template node so the projection can surface each run
        // node's NodeType — the live-view Halt affordance is AI-node only.
        var runNodes = await _loopRunStore.GetRunNodesWithNodeAsync(guid);
        var sessionSnapshots = await _sessionSnapshotStore.ListMetadataForRunAsync(guid);
        var sessionBindings = await _loopRunStore.GetSessionBindingsAsync(guid);
        var variables = await _loopRunStore.GetVariablesAsync(guid);
        var missingGlobals = await MissingGlobalVariablesAsync(run, new ParkReads(_templates, _globals));
        var historyKeptUntil = await HistoryKeptUntilAsync(run);
        var currentSessionIds = sessionBindings
            .Select(b => $"{b.AdapterName}\n{b.SessionId}")
            .ToHashSet(StringComparer.Ordinal);
        var totals = RunCostTotals.From(runNodes);

        return Ok(new
        {
            id = run.Id,
            workItemId = run.WorkItemId,
            loopTemplateId = run.LoopTemplateVersion?.LoopTemplateId,
            templateVersion = run.LoopTemplateVersion?.VersionNumber ?? 0,
            status = run.Status.ToString(),
            currentNodeId = run.CurrentNodeId,
            isPaused = run.IsPaused,
            isHalted = run.IsHalted,
            haltReason = run.HaltReason?.ToString(),
            // Read now, not at the park, so it drops each one a person has set since.
            missingGlobalVariables = missingGlobals,
            retain = run.Retain,
            worktreePath = run.WorktreePath,
            branchName = run.BranchName,
            hasLocalGitState = HasLocalGitState(run),
            nodeExecutionCount = run.NodeExecutionCount,
            // AI nodes run since the last human touch. The steer window shows it
            // on a MaxAiTraversals park so the person is told how far it got.
            aiTraversalCount = run.AiTraversalCount,
            startedAt = run.StartedAt,
            completedAt = run.CompletedAt,
            historyKeptUntil,
            prUrl = run.PrUrl,
            // Embedded as JSON (camelCase from the poller) so the feedback UI can
            // render the full PR view; null until the heartbeat poller's first pass.
            prSnapshot = ParsePrSnapshot(run.PrSnapshot),
            // What this round intends to write on the pull request but has not
            // written yet. Visible from the moment an agent queues it until the
            // PR node drains it, which is the window a person has to drop any of
            // it — so it is sent whatever the run's status.
            prQueuedWrites = PrCommentQueueJson.TryParse(run.PrCommentQueue).Select(q => new
            {
                id = q.Id,
                kind = q.Kind,
                targetId = q.TargetId,
                body = q.Body,
                path = q.Path,
                line = q.Line,
                queuedAt = q.QueuedAt,
            }),
            prAutoMerge = run.PrAutoMerge?.ToString(),
            prAutoMergeError = run.PrAutoMergeError,
            totalInputTokens = totals.TotalInputTokens,
            totalOutputTokens = totals.TotalOutputTokens,
            totalCostUsd = totals.TotalCostUsd,
            availableSessions = sessionSnapshots.Select(s => new
            {
                adapterName = s.AdapterName,
                sessionId = s.SessionId,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt,
                // Legacy (remove with migration B): unknown until the row's content is converted, so
                // shown as nothing rather than guessed.
                format = s.Unconverted ? null : s.Format,
                byteLength = s.Unconverted ? (long?)null : s.ByteLength,
                eventCount = s.Unconverted ? (int?)null : s.EventCount,
                isCurrent = currentSessionIds.Contains($"{s.AdapterName}\n{s.SessionId}"),
                placeholders = sessionBindings
                    .Where(b => b.AdapterName == s.AdapterName && b.SessionId == s.SessionId)
                    .Select(b => b.PlaceholderId)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList(),
            }).ToList(),
            availableVariables = variables.Select(v => new
            {
                name = v.Name,
                value = v.Value,
                createdAt = v.CreatedAt,
                updatedAt = v.UpdatedAt,
            }).ToList(),
            nodes = runNodes.Select(LoopRunNodeResponse.From).ToList(),
        });
    }

    // The PR snapshot is stored as a JSON string; surface it as a JSON value so
    // it embeds in the response object rather than as an escaped string. A
    // corrupt blob degrades to null rather than failing the whole response.
    private static JsonElement? ParsePrSnapshot(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Drop one of the pull-request writes this round intends to make, before
    /// the PR node makes it. Nothing is lost by dropping: the thread stays open
    /// and the finding stays undelivered, so a later review raises it again.
    /// </summary>
    [HttpDelete("{id}/pr-queue/{writeId}")]
    public async Task<IActionResult> DropQueuedPrWrite(
        string id, string writeId, [FromServices] IPrWriteQueue queue)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        return await queue.DropQueuedAsync(guid, writeId)
            ? Ok(new { dropped = writeId })
            : NotFound(new { error = $"No queued pull-request write '{writeId}' on this run." });
    }

    [HttpPost("{id}/pause")]
    public async Task<IActionResult> Pause(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _loopEngine.PauseRunAsync(guid);
        return Ok();
    }

    [HttpPost("{id}/resume")]
    public async Task<IActionResult> Resume(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _loopEngine.ResumeRunAsync(guid);
        return Ok();
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _loopEngine.CancelRunAsync(guid);
        return Ok();
    }

    [HttpPost("{id}/halt")]
    public async Task<IActionResult> Halt(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _loopEngine.HaltRunAsync(guid);
        return Ok();
    }

    public sealed class ResumeSteerRequest
    {
        public string? Note { get; set; }
    }

    [HttpPost("{id}/resume-steer")]
    public async Task<IActionResult> ResumeSteer(string id, [FromBody] ResumeSteerRequest? request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        await _loopEngine.ResumeFromHaltAsync(guid, request?.Note);
        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();
        if (run.Status == ILD.Data.Enums.LoopRunStatus.Running)
            return BadRequest(new { error = "Cannot delete a running loop. Cancel it first." });
        if (User.Identity?.Name is not { Length: > 0 } username)
            return Unauthorized();
        var user = WorkItemActor.Human(username);

        // The row may only go once the git state it names is verifiably gone —
        // a deleted row would leave the leftovers orphaned with nothing
        // pointing at them.
        if (!await _runReclaimer.ReclaimLocalStateAsync(run))
            return Conflict(new { error = "Could not reclaim the run's worktree/branch; the run was not deleted. Retry, or check server logs." });

        // Its pending edit proposals go with it, or nothing would ever decide them.
        try
        {
            await _workItemManager.WithdrawPendingProposalsOfRunAsync(user, guid, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { error = "WorkItemServer unreachable: the run's pending edit proposals could not be withdrawn, so the run was not deleted. Retry later.", detail = ex.Message });
        }

        var deleted = await _loopRunStore.DeleteAsync(guid);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Reclaim a finished run's worktree, local branch and AI session snapshots
    /// while keeping the run row and its history — the non-destructive
    /// counterpart of <c>Delete</c>. An ended run loses these on its own unless
    /// it is pinned; this frees them now, pinned or not, and for a Failed run.
    /// Unrelated to the Cleanup <em>node</em>, which ends a run rather than
    /// reclaiming one.
    /// </summary>
    [HttpPost("{id}/cleanup")]
    public async Task<IActionResult> Cleanup(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();

        // Stricter than Delete, which only refuses a Running run (see RunCleanup).
        return await RunCleanup.CleanUpAsync(run, _runReclaimer, _loopRunStore, _sessionSnapshotStore, _claudeProjects) switch
        {
            RunCleanupOutcome.NotFinished => BadRequest(new { error = "Cannot clean up a run that has not finished. Let it finish, or cancel it first." }),
            RunCleanupOutcome.ReclaimFailed => Conflict(new { error = "Could not remove the run's worktree/branch. Retry, or check server logs." }),
            RunCleanupOutcome.RunGone => NotFound(),
            _ => NoContent(),
        };
    }

    [HttpPost("{id}/nodes/{runNodeId}/retry")]
    public async Task<IActionResult> RetryFromNode(string id, string runNodeId)
    {
        if (!Guid.TryParse(id, out var runGuid))
            return BadRequest(new { error = "Invalid run GUID" });
        if (!Guid.TryParse(runNodeId, out var nodeGuid))
            return BadRequest(new { error = "Invalid run node GUID" });

        try
        {
            await _loopEngine.RetryFromNodeAsync(runGuid, nodeGuid);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        return Ok();
    }

    public sealed class RetainRequest
    {
        public bool Retain { get; set; }
    }

    /// <summary>
    /// Pin or unpin a run. A pinned run (<c>retain = true</c>) keeps everything:
    /// its worktree, branch and session snapshots are not removed when it ends,
    /// and neither they nor its history are touched by the retention sweeper,
    /// until the mark is cleared. The next sweep after that removes its leftovers.
    /// </summary>
    [HttpPut("{id}/retain")]
    public async Task<IActionResult> SetRetain(string id, [FromBody] RetainRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        if (!await _loopRunStore.SetRetainAsync(guid, request.Retain)) return NotFound();
        return Ok(new { id = guid, retain = request.Retain });
    }

    [HttpGet("{id}/events")]
    public async Task<IActionResult> GetEvents(string id, [FromQuery] long cursor = 0, [FromQuery] int limit = 100)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;
        if (cursor < 0) cursor = 0;

        var page = await _eventLogService.GetByRunIdAfterCursorAsync(guid, cursor, limit);
        return Ok(new
        {
            entries = page.Entries.Select(e => new
            {
                id = e.Id,
                runId = e.LoopRunId,
                eventType = e.EventType.ToString(),
                nodeId = e.NodeId,
                runNodeId = e.RunNodeId,
                edgeName = e.EdgeName,
                timestamp = e.Timestamp,
                payload = e.Data ?? string.Empty
            }),
            nextCursor = page.NextCursor,
            hasMore = page.HasMore
        });
    }

    /// <summary>
    /// The run's conversation, projected from its event log in the order it was
    /// written: all of it, or what follows event <paramref name="after"/>.
    /// <c>lastEventId</c> is where the next read goes on from.
    /// </summary>
    [HttpGet("{id}/conversation")]
    public async Task<IActionResult> GetConversation(string id, [FromQuery] long after = 0)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        if (after < 0)
            return BadRequest(new { error = "after must not be negative" });

        var page = await _conversation.GetPageAsync(guid, after);
        if (page is null) return NotFound();
        return Ok(new { runId = guid, messages = page.Messages, lastEventId = page.LastEventId });
    }

    private const int DefaultPreviewEvents = 200;
    private const int MaxPreviewEvents = 500;

    /// <summary>
    /// A page of a saved session: of a JSONL session, the last <paramref name="tail"/> lines before
    /// <paramref name="beforeOffset"/> (the end by default), which is how a reader pages back from
    /// the latest events, reading only the chunks that hold them; of a session that is one
    /// document, its start as a single event. A session still in the legacy column is not
    /// converted here: the page is empty and says it is converting.
    /// </summary>
    [HttpGet("{id}/sessions/preview")]
    public async Task<IActionResult> GetSessionPreview(
        string id,
        [FromQuery] string adapterName,
        [FromQuery] string sessionId,
        [FromQuery] int tail = DefaultPreviewEvents,
        [FromQuery] long? beforeOffset = null)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "adapterName and sessionId are required" });

        if (tail is < 1 or > MaxPreviewEvents)
            return BadRequest(new { error = $"tail must be between 1 and {MaxPreviewEvents}" });
        if (beforeOffset < 0)
            return BadRequest(new { error = "beforeOffset must not be negative" });

        // Legacy (remove with migration B): found in the listing, which never converts, rather
        // than through GetMetadataAsync, which would.
        var snapshot = (await _sessionSnapshotStore.ListMetadataForRunAsync(guid))
            .FirstOrDefault(s => s.AdapterName == adapterName && s.SessionId == sessionId);
        if (snapshot == null)
            return NotFound(new { error = "Session snapshot not found" });

        // Legacy (remove with migration B): converting a long session takes seconds, so a preview
        // leaves it to the background migration and says so; a converted row never goes back.
        // The page carries the metadata it was read with, so a turn saved meanwhile cannot
        // describe one version and show another.
        SessionSnapshotPage? page = null;
        if (!snapshot.Unconverted)
        {
            var lines = SessionSnapshotFormats.IsLines(snapshot.Format);
            if (beforeOffset is not null && !lines)
                return BadRequest(new { error = "beforeOffset only applies to a session of lines" });
            if (beforeOffset > snapshot.ByteLength)
                return BadRequest(new { error = "beforeOffset must not be past the end of the session" });

            var key = SessionSnapshotKey.ForRun(guid, adapterName, sessionId);
            page = lines
                ? await _sessionSnapshotStore.ReadTailAsync(key, tail, beforeOffset)
                : await _sessionSnapshotStore.ReadHeadAsync(key);
            if (page == null)
                return NotFound(new { error = "Session snapshot not found" });
        }

        return Ok(new
        {
            adapterName = snapshot.AdapterName,
            sessionId = snapshot.SessionId,
            createdAt = snapshot.CreatedAt,
            updatedAt = page?.Snapshot.UpdatedAt ?? snapshot.UpdatedAt,
            // Legacy (remove with migration B): null while converting, as in the sessions list.
            format = page?.Snapshot.Format,
            byteLength = page?.Snapshot.ByteLength,
            eventCount = page?.Snapshot.EventCount,
            events = page?.Events ?? [],
            fromOffset = page?.FromOffset ?? 0,
            previousOffset = page?.PreviousOffset,
            truncated = page?.Truncated ?? false,
            converting = page is null,
        });
    }

    [HttpGet("{id}/terminal")]
    public async Task<IActionResult> OpenWorktreeTerminal(string id, [FromQuery] int cols = 120, [FromQuery] int rows = 30)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "Expected WebSocket upgrade request." });
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run is null) return NotFound();
        // A live worktree is inspectable for any run that has stopped executing
        // — parked for review (WaitingHuman), failed (e.g. a node hitting a
        // session/usage limit), or cancelled — so a human can read the diff or
        // recover work. Only a still-Running run is off-limits: its executor is
        // actively writing the worktree, so a concurrent shell would race it.
        if (run.Status == ILD.Data.Enums.LoopRunStatus.Running)
            return BadRequest(new { error = "Terminal is not available while the run is still executing." });
        if (string.IsNullOrWhiteSpace(run.WorktreePath) || !Directory.Exists(run.WorktreePath))
            return BadRequest(new { error = "Run has no live worktree." });

        // The worktree is the run's, so its shell gets the feeds the run's processes get.
        var feeds = await _packageFeeds.ResolveAsync(
            await RunRepository.IdOfAsync(run, _workItemManager), HttpContext.RequestAborted);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _shellSessions.RunAsync(
            socket, run.WorktreePath, run.Id.ToString("N"), cols, rows, feeds.Feeds, HttpContext.RequestAborted);
        return new EmptyResult();
    }
}
