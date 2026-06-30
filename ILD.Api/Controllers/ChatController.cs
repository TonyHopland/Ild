using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// REST surface for the chat bubble (ADR-0010, retained history ADR-0013). The
/// streaming of a turn happens over the <c>/hubs/chat</c> SignalR hub; these
/// endpoints start chats, list/resume retained history, submit messages (which
/// interrupt any in-flight turn rather than queueing), and delete chats (one or
/// all). A chat is never deleted automatically — only by an explicit delete.
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

        // Resolve the target chat scoped by user, so a message can only ever drive a
        // chat the caller owns.
        var session = await _chat.GetByIdAsync(userId, id, ct);
        if (session is null)
            return NotFound(new { error = "Chat not found." });

        await _runner.SubmitAsync(session.Id, request.Content, request.OpenWorkItemId, request.OpenLoopDocument);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        await _runner.InterruptAsync(id);
        var deleted = await _chat.DeleteAsync(userId, id, ct);
        return deleted ? NoContent() : NotFound();
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

        if (HttpContext.Items.TryGetValue("IsAgent", out var isAgent) && isAgent is true)
        {
            error = Forbid();
            return false;
        }

        var username = HttpContext.Items["Username"] as string;
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
