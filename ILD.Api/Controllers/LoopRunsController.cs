using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class LoopRunsController : ControllerBase
{
    private readonly ILoopEngine _loopEngine;
    private readonly IEventLogService _eventLogService;

    public LoopRunsController(ILoopEngine loopEngine, IEventLogService eventLogService)
    {
        _loopEngine = loopEngine;
        _eventLogService = eventLogService;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var status = await _loopEngine.GetRunStatusAsync(guid);
        return Ok(new { status });
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

    [HttpGet("{id}/events")]
    public async Task<IActionResult> GetEvents(string id, [FromQuery] int cursor = 0, [FromQuery] int limit = 100)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var page = await _eventLogService.GetByRunIdAfterCursorAsync(guid, cursor, limit);
        return Ok(new
        {
            entries = page.Entries.Select(e => new
            {
                sequence = e.Sequence,
                runId = e.LoopRunId,
                eventType = e.EventType.ToString(),
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
}
