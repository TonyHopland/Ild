using ILD.Core.Services.Implementations;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The signed-in user's Chat Schedules (ADR-0026): each starts a chat turn of
/// theirs on a cron. Scoped by owner, so another user's schedule is not found,
/// whatever the verb. A schedule that is firing or being changed answers a
/// further change or Run now with a conflict rather than making it wait.
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
        var created = await _schedules.CreateAsync(userId, request, ct);
        return created.Value is null ? Refused(created) : StatusCode(StatusCodes.Status201Created, created.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ChatScheduleRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var updated = await _schedules.UpdateAsync(userId, id, request, ct);
        return updated.Value is null ? Refused(updated) : Ok(updated.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        return await _schedules.DeleteAsync(userId, id, ct) switch
        {
            null => NoContent(),
            ChatScheduleRefusal.Busy => Conflict(new { error = ChatScheduleService.BusyError }),
            _ => NotFound(),
        };
    }

    /// <summary>Fires the schedule once now, paused or disabled; answers with the firing, which may be a skip.</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var fired = await _schedules.RunNowAsync(userId, id, ct);
        return fired.Value is null ? Refused(fired) : Ok(fired.Value);
    }

    private IActionResult Refused<T>(ChatScheduleResult<T> result) where T : class
        => result.Refusal switch
        {
            ChatScheduleRefusal.Invalid => BadRequest(new { error = result.Error }),
            ChatScheduleRefusal.Busy => Conflict(new { error = result.Error }),
            _ => NotFound(),
        };

    // Agents never reach here: schedules belong to the user-only surface, so the
    // fallback policy has already turned an agent token away with a 403.
    private bool TryResolveUser(out string userId, out IActionResult error)
    {
        userId = User.Identity?.Name ?? string.Empty;
        error = Unauthorized();
        return userId.Length > 0;
    }
}
