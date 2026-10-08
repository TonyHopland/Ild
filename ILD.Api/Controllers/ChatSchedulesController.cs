using ILD.Core.Services.Implementations;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The signed-in user's chat schedules (ADR-0025): each one starts a chat turn of
/// theirs on a cron. Scoped by owner, so another user's schedule is not found,
/// whatever the verb.
/// </summary>
[ApiController]
[Route("api/v1/chat/schedules")]
public class ChatSchedulesController : ControllerBase
{
    private readonly ChatScheduleService _schedules;

    public ChatSchedulesController(ChatScheduleService schedules)
    {
        _schedules = schedules;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        return Ok(await _schedules.ListAsync(userId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ChatScheduleRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var saved = await _schedules.CreateAsync(userId, request, ct);
        return saved.Schedule is null
            ? BadRequest(new { error = saved.Error })
            : CreatedAtAction(nameof(List), saved.Schedule);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ChatScheduleRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var saved = await _schedules.UpdateAsync(userId, id, request, ct);
        if (saved is null) return NotFound();
        return saved.Schedule is null ? BadRequest(new { error = saved.Error }) : Ok(saved.Schedule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        return await _schedules.DeleteAsync(userId, id, ct) ? NoContent() : NotFound();
    }

    /// <summary>Fires the schedule once now, paused or not; answers with the firing.</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var firing = await _schedules.RunNowAsync(userId, id, ct);
        return firing is null ? NotFound() : Ok(firing);
    }

    private bool TryResolveUser(out string userId, out IActionResult error)
    {
        userId = User.Identity?.Name ?? string.Empty;
        error = Unauthorized();
        return userId.Length > 0;
    }
}
