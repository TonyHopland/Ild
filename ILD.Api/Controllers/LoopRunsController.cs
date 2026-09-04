using System.Text.Json;
using ILD.Api.Contracts;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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

    public LoopRunsController(
        ILoopEngine loopEngine,
        IEventLogService eventLogService,
        ILoopRunStore loopRunStore,
        IAdapterSessionSnapshotStore sessionSnapshotStore,
        InteractiveShellSessionService shellSessions,
        IRunReclaimer runReclaimer)
    {
        _loopEngine = loopEngine;
        _eventLogService = eventLogService;
        _loopRunStore = loopRunStore;
        _sessionSnapshotStore = sessionSnapshotStore;
        _shellSessions = shellSessions;
        _runReclaimer = runReclaimer;
    }

    /// <summary>
    /// Whether the run still owns a worktree or local branch to reclaim. The
    /// answer rather than the paths: it is all the cleanup affordance needs,
    /// and a run's worktree path is an absolute server path.
    /// </summary>
    private static bool HasLocalGitState(ILD.Data.Entities.LoopRun run)
        => !string.IsNullOrEmpty(run.WorktreePath) || !string.IsNullOrEmpty(run.BranchName);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var runs = await _loopRunStore.GetAllAsync(skip, take);
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
        var sessionSnapshots = await _loopRunStore.GetSessionSnapshotsAsync(guid);
        var sessionBindings = await _loopRunStore.GetSessionBindingsAsync(guid);
        var variables = await _loopRunStore.GetVariablesAsync(guid);
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
            prUrl = run.PrUrl,
            // Embedded as JSON (camelCase from the poller) so the feedback UI can
            // render the full PR view; null until the heartbeat poller's first pass.
            prSnapshot = ParsePrSnapshot(run.PrSnapshot),
            totalInputTokens = totals.TotalInputTokens,
            totalOutputTokens = totals.TotalOutputTokens,
            totalCostUsd = totals.TotalCostUsd,
            availableSessions = sessionSnapshots.Select(s => new
            {
                adapterName = s.AdapterName,
                sessionId = s.SessionId,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt,
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
    public async Task<IActionResult> Delete(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();
        if (run.Status == ILD.Data.Enums.LoopRunStatus.Running)
            return BadRequest(new { error = "Cannot delete a running loop. Cancel it first." });

        // The row may only go once the git state it names is verifiably gone —
        // a deleted row would leave the leftovers orphaned with nothing
        // pointing at them.
        if (!await _runReclaimer.ReclaimLocalStateAsync(run))
            return Conflict(new { error = "Could not reclaim the run's worktree/branch; the run was not deleted. Retry, or check server logs." });

        var deleted = await _loopRunStore.DeleteAsync(guid);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Reclaim a finished run's worktree and local branch while keeping the run
    /// row and its history — the non-destructive counterpart of <c>Delete</c>,
    /// for freeing a branch name a later run wants to reuse. Unrelated to the
    /// Cleanup <em>node</em>, which ends a run rather than reclaiming one.
    /// </summary>
    [HttpPost("{id}/cleanup")]
    public async Task<IActionResult> Cleanup(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();
        // Stricter than Delete: resuming a WaitingHuman run is its expected next
        // step and re-enters at CurrentNodeId without passing through Start, so
        // it would not re-create what this destroys. A finished run's ↻ Retry
        // would — or fails cleanly on a node that needs a worktree.
        if (run.Status is ILD.Data.Enums.LoopRunStatus.Running or ILD.Data.Enums.LoopRunStatus.WaitingHuman)
            return BadRequest(new { error = "Cannot clean up a run that has not finished. Let it finish, or cancel it first." });

        if (!await _runReclaimer.ReclaimLocalStateAsync(run))
            return Conflict(new { error = "Could not reclaim the run's worktree/branch; the run was left untouched. Retry, or check server logs." });

        // The row now outlives the git state it names, so the pointers go with
        // it: a surviving BranchName would keep claiming a branch that is free,
        // and a surviving WorktreePath would name a directory that is gone.
        run.WorktreePath = null;
        run.BranchName = null;
        await _loopRunStore.UpdateRunAsync(run);
        return NoContent();
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
    /// Pin or unpin a run. A pinned run (<c>retain = true</c>) is never reclaimed
    /// by the worktree retention sweeper — its worktree, branch, and history are
    /// kept until the mark is cleared.
    /// </summary>
    [HttpPut("{id}/retain")]
    public async Task<IActionResult> SetRetain(string id, [FromBody] RetainRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });
        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();
        run.Retain = request.Retain;
        await _loopRunStore.UpdateRunAsync(run);
        return Ok(new { id = run.Id, retain = run.Retain });
    }

    [HttpGet("{id}/events")]
    public async Task<IActionResult> GetEvents(string id, [FromQuery] int cursor = 0, [FromQuery] int limit = 100)
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
                sequence = e.Sequence,
                runId = e.LoopRunId,
                eventType = e.EventType.ToString(),
                nodeId = e.NodeId,
                runNodeId = e.RunNodeId,
                timestamp = e.Timestamp,
                payload = e.Data ?? string.Empty
            }),
            nextCursor = page.NextCursor,
            hasMore = page.HasMore
        });
    }

    [HttpGet("{id}/sessions/preview")]
    public async Task<IActionResult> GetSessionPreview(
        string id,
        [FromQuery] string adapterName,
        [FromQuery] string sessionId)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        if (string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "adapterName and sessionId are required" });

        var snapshot = await _sessionSnapshotStore.GetAsync(guid, adapterName, sessionId);
        if (snapshot == null)
            return NotFound(new { error = "Session snapshot not found" });

        return Ok(new
        {
            adapterName = snapshot.AdapterName,
            sessionId = snapshot.SessionId,
            createdAt = snapshot.CreatedAt,
            updatedAt = snapshot.UpdatedAt,
            sessionJson = snapshot.SessionJson,
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

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _shellSessions.RunAsync(socket, run.WorktreePath, run.Id.ToString("N"), cols, rows, HttpContext.RequestAborted);
        return new EmptyResult();
    }
}
