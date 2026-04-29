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
    public async Task<IActionResult> GetEvents(string id, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (!Guid.TryParse(id, out var guid))
            return BadRequest(new { error = "Invalid GUID" });

        var events = await _eventLogService.GetByRunIdAsync(guid, take);
        return Ok(events);
    }
}
