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

    public LoopRunsController(
        ILoopEngine loopEngine,
        IEventLogService eventLogService,
        ILoopRunStore loopRunStore,
        IAdapterSessionSnapshotStore sessionSnapshotStore)
    {
        _loopEngine = loopEngine;
        _eventLogService = eventLogService;
        _loopRunStore = loopRunStore;
        _sessionSnapshotStore = sessionSnapshotStore;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;
        if (take > 500) take = 500;
        var runs = await _loopRunStore.GetAllAsync(skip, take);
        var result = runs.Select(r => new
        {
            id = r.Id,
            workItemId = r.WorkItemId,
            loopTemplateId = r.LoopTemplateVersion?.LoopTemplateId,
            templateVersion = r.LoopTemplateVersion?.VersionNumber ?? 0,
            status = r.Status.ToString(),
            currentNodeId = r.CurrentNodeId,
            isPaused = r.IsPaused,
            nodeExecutionCount = r.NodeExecutionCount,
            startedAt = r.StartedAt,
            completedAt = r.CompletedAt,
            nodes = r.RunNodes.OrderBy(rn => rn.CreatedAt).Select(rn => new
            {
                id = rn.Id,
                nodeId = rn.LoopNodeId,
                nodeLabel = rn.NodeLabel ?? rn.LoopNode?.Label ?? string.Empty,
                status = rn.Status.ToString(),
                effectiveInput = rn.EffectiveInput,
                output = rn.Output,
                error = rn.Error,
                startedAt = rn.StartedAt,
                completedAt = rn.CompletedAt,
                executionCount = 0,
            }).ToList(),
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

        var runNodes = await _loopRunStore.GetRunNodesAsync(guid);
        var sessionSnapshots = await _loopRunStore.GetSessionSnapshotsAsync(guid);
        var sessionBindings = await _loopRunStore.GetSessionBindingsAsync(guid);
        var currentSessionIds = sessionBindings
            .Select(b => $"{b.AdapterName}\n{b.SessionId}")
            .ToHashSet(StringComparer.Ordinal);

        return Ok(new
        {
            id = run.Id,
            workItemId = run.WorkItemId,
            loopTemplateId = run.LoopTemplateVersion?.LoopTemplateId,
            templateVersion = run.LoopTemplateVersion?.VersionNumber ?? 0,
            status = run.Status.ToString(),
            currentNodeId = run.CurrentNodeId,
            isPaused = run.IsPaused,
            nodeExecutionCount = run.NodeExecutionCount,
            startedAt = run.StartedAt,
            completedAt = run.CompletedAt,
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
            nodes = runNodes.Select(rn => new
            {
                id = rn.Id,
                nodeId = rn.LoopNodeId,
                nodeLabel = rn.NodeLabel ?? rn.LoopNode?.Label ?? string.Empty,
                status = rn.Status.ToString(),
                effectiveInput = rn.EffectiveInput,
                output = rn.Output,
                error = rn.Error,
                startedAt = rn.StartedAt,
                completedAt = rn.CompletedAt,
                executionCount = 0,
            }).ToList(),
        });
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

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var run = await _loopRunStore.GetByIdAsync(guid);
        if (run == null) return NotFound();
        if (run.Status == ILD.Data.Enums.LoopRunStatus.Running)
            return BadRequest(new { error = "Cannot delete a running loop. Cancel it first." });

        var deleted = await _loopRunStore.DeleteAsync(guid);
        return deleted ? NoContent() : NotFound();
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
                payload = e.Data ?? string.Empty,
                hasPayload = !string.IsNullOrEmpty(e.PayloadPath)
            }),
            nextCursor = page.NextCursor,
            hasMore = page.HasMore
        });
    }

    [HttpGet("{id}/events/payload")]
    public async Task<IActionResult> GetPayload(string id, [FromQuery] int sequence)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var entry = await _eventLogService.GetBySequenceAsync(guid, sequence);
        if (entry == null)
            return NotFound(new { error = "Event not found" });

        if (string.IsNullOrEmpty(entry.PayloadPath))
            return Ok(new { payload = entry.Data ?? string.Empty });

        if (!System.IO.File.Exists(entry.PayloadPath))
            return NotFound(new { error = "Payload file not found" });

        var content = await System.IO.File.ReadAllTextAsync(entry.PayloadPath);
        return Ok(new { payload = content });
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
}
