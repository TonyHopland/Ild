using ILD.Data.DTOs;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// REST surface for the chat bubble (ADR-0010, retained history ADR-0013). The
/// streaming of a turn happens over the <c>/hubs/chat</c> SignalR hub; these
/// endpoints start chats, list/resume retained history, submit messages (which
/// interrupt any in-flight turn rather than queueing), cancel an in-flight turn
/// on its own, record how far a chat has been read, rename, star and search
/// chats, and delete chats (one or all). A chat is never deleted automatically — only by an
/// explicit delete.
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
        return Ok(chats.Select(c => c with { IsBusy = _runner.ActiveTurnId(c.Id) is not null }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var session = await _chat.GetByIdAsync(userId, id, ct);
        if (session is null) return NotFound();

        // Turn liveness lives in the runner, not in the stored session, and a bubble
        // that has just loaded or just reconnected has no other way to learn this
        // chat is mid-turn. Read after the ownership check, so an answer about a
        // turn is only ever given about a chat the caller owns.
        return Ok(session with { ActiveTurnId = _runner.ActiveTurnId(id) });
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartChatRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (!Guid.TryParse(request.AiProviderId, out var providerId))
            return BadRequest(new { error = "A valid aiProviderId is required." });

        try
        {
            var session = await _chat.StartAsync(userId, providerId, request.Tools, ct);
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

        // The turn id goes back with the acceptance, so the client that sent the
        // message knows which turn is in flight without waiting to be told over the
        // hub. Read after the ownership check above, like every other answer about a
        // turn here.
        var turnId = await _runner.SubmitAsync(id, request.Content, request.OpenWorkItemId, request.OpenLoopDocument);
        return Accepted(new ChatSendAcceptedView(turnId));
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

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, [FromBody] MarkChatReadRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (request.Sequence is not { } sequence || sequence < 0)
            return BadRequest(new { error = "A non-negative sequence is required." });

        if (!await _chat.ExistsForUserAsync(userId, id, ct))
            return NotFound();

        // A sequence at or below the stored marker is not an error: another tab or
        // a later send may already have read further.
        await _chat.MarkReadAsync(userId, id, sequence, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/name")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameChatRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        // NUL goes before the length is judged: the rename is a conditional update,
        // which the save-time scrub never sees.
        var name = request.Name is null ? null : ChatTitles.WithoutNul(request.Name).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > RenameChatRequest.MaxNameLength)
            return BadRequest(new { error = $"A name of 1 to {RenameChatRequest.MaxNameLength} characters is required." });

        // Scoped by owner, so another user's chat reads as missing, like an unknown one.
        return await _chat.RenameAsync(userId, id, name, ct) ? NoContent() : NotFound();
    }

    [HttpPut("{id:guid}/favorite")]
    public async Task<IActionResult> SetFavorite(Guid id, [FromBody] SetChatFavoriteRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (request.Favorite is not { } favorite)
            return BadRequest(new { error = "A boolean favorite is required." });

        // Scoped by owner, so another user's chat reads as missing, like an unknown one.
        return await _chat.SetFavoriteAsync(userId, id, favorite, ct) ? NoContent() : NotFound();
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        // Stored text never holds NUL (the save-time scrub), and the database cannot take it.
        var query = q is null ? null : ChatTitles.WithoutNul(q).Trim();
        if (string.IsNullOrEmpty(query))
            return BadRequest(new { error = "A search query is required." });

        return Ok(await _chat.SearchForUserAsync(userId, query, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        // Confirm ownership before touching the chat, so interrupting an in-flight
        // turn can never act on another user's session.
        if (!await _chat.ExistsForUserAsync(userId, id, ct))
            return NotFound();

        // Deleted while holding the chat's turn gate, so a message sent meanwhile
        // cannot start a turn that recreates what the delete removes.
        await _runner.DeleteAsync(id, () => _chat.DeleteAsync(userId, id, ct));
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        // Each chat is deleted while holding its turn gate (see Delete), so neither a
        // streaming reply nor a message sent meanwhile outlives it.
        var chats = await _chat.ListForUserAsync(userId, ct);
        foreach (var chat in chats)
            await _runner.DeleteAsync(chat.Id, () => _chat.DeleteAsync(userId, chat.Id, ct));
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

public sealed class RenameChatRequest
{
    // The stored name's column length.
    public const int MaxNameLength = 120;

    public string? Name { get; set; }
}

public sealed class SetChatFavoriteRequest
{
    public bool? Favorite { get; set; }
}

public sealed class MarkChatReadRequest
{
    /// <summary>The highest message sequence the user has seen in the chat.</summary>
    public int? Sequence { get; set; }
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
    /// The live <c>ild-loop-template/v2</c> document of the loop open in the Loop
    /// Editor when sending this message, or null when none is open (loop editor
    /// context, ADR-0011). Stashed server-side in the per-session loop scratchpad,
    /// overwritten every message; the agent reads it on demand via
    /// <c>get_current_loop</c>. Carries the diverged, possibly-unsaved client state,
    /// not the persisted version.
    /// </summary>
    public string? OpenLoopDocument { get; set; }
}
