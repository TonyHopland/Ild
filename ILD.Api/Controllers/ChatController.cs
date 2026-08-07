using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// REST surface for the chat bubble (ADR-0010, retained history ADR-0013). The
/// streaming of a turn happens over the <c>/hubs/chat</c> SignalR hub; these
/// endpoints start chats, list/resume retained history, submit messages (which
/// interrupt any in-flight turn rather than queueing), cancel an in-flight turn
/// on its own, and delete chats (one or all). A chat is never deleted
/// automatically — only by an explicit delete.
/// </summary>
[ApiController]
[Route("api/v1/chat")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chat;
    private readonly IChatTurnRunner _runner;

    public ChatController(IChatService chat, IChatTurnRunner runner)
    {
        _chat = chat;
        _runner = runner;
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var chats = await _chat.ListForUserAsync(userId, ct);
        return Ok(chats);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var session = await _chat.GetByIdAsync(userId, id, ct);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartChatRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (!Guid.TryParse(request.AiProviderId, out var providerId))
            return BadRequest(new { error = "A valid aiProviderId is required." });

        try
        {
            var session = await _chat.StartAsync(userId, providerId, request.Tools ?? Array.Empty<string>(), ct);
            return Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] ChatMessageRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Message content is required." });

        // Authorize against the target chat (scoped by user) before driving it, so a
        // message can only ever reach a chat the caller owns.
        if (!await _chat.ExistsForUserAsync(userId, id, ct))
            return NotFound(new { error = "Chat not found." });

        await _runner.SubmitAsync(id, request.Content, request.OpenWorkItemId, request.OpenLoopDocument);
        return Accepted();
    }

    [HttpPost("{id:guid}/interrupt")]
    public async Task<IActionResult> Interrupt(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        // Confirm ownership before cancelling, so a stop can never reach another
        // user's in-flight turn.
        if (!await _chat.ExistsForUserAsync(userId, id, ct))
            return NotFound();

        // Cancelling is idempotent: with no turn in flight the runner no-ops, so a
        // stop that races the turn finishing is still Accepted.
        await _runner.InterruptAsync(id);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        // Confirm ownership before touching the chat, so interrupting an in-flight
        // turn can never act on another user's session.
        if (!await _chat.ExistsForUserAsync(userId, id, ct))
            return NotFound();

        await _runner.InterruptAsync(id);
        await _chat.DeleteAsync(userId, id, ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        // Cancel any in-flight turns first so a delete can't race a streaming reply.
        var chats = await _chat.ListForUserAsync(userId, ct);
        foreach (var chat in chats)
            await _runner.InterruptAsync(chat.Id);

        await _chat.DeleteAllForUserAsync(userId, ct);
        return NoContent();
    }

    private bool TryResolveUser(out string userId, out IActionResult error)
    {
        userId = string.Empty;
        error = Unauthorized();

        // Agents never reach here: chats belong to the user-only surface, so the
        // fallback policy has already turned an agent token away with a 403.
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return false;

        userId = username;
        return true;
    }
}

public sealed class StartChatRequest
{
    public string AiProviderId { get; set; } = string.Empty;
    public string[]? Tools { get; set; }
}

public sealed class ChatMessageRequest
{
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The ambient per-turn Chat Context (ADR-0011): the id of the work item the
    /// user has open when sending this message, or null when none is open. A thin
    /// pointer only — the agent pulls the heavy data via tools on demand.
    /// </summary>
    public string? OpenWorkItemId { get; set; }

    /// <summary>
    /// The live <c>ild-loop-template/v1</c> document of the loop open in the Loop
    /// Editor when sending this message, or null when none is open (loop editor
    /// context, ADR-0011). Stashed server-side in the per-session loop scratchpad,
    /// overwritten every message; the agent reads it on demand via
    /// <c>get_current_loop</c>. Carries the diverged, possibly-unsaved client state,
    /// not the persisted version.
    /// </summary>
    public string? OpenLoopDocument { get; set; }
}
