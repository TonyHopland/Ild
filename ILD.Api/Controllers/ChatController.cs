using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// REST surface for the per-user chat bubble (ADR-0010). The streaming of a turn
/// happens over the <c>/hubs/chat</c> SignalR hub; these endpoints start/end the
/// session, rehydrate the transcript, and submit messages (which interrupt any
/// in-flight turn rather than queueing).
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

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        var session = await _chat.GetForUserAsync(userId, ct);
        return session is null ? NoContent() : Ok(session);
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

    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] ChatMessageRequest request, CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Message content is required." });

        var session = await _chat.GetForUserAsync(userId, ct);
        if (session is null)
            return NotFound(new { error = "No active chat session. Start one first." });

        await _runner.SubmitAsync(session.Id, request.Content, request.OpenWorkItemId);
        return Accepted();
    }

    [HttpDelete]
    public async Task<IActionResult> End(CancellationToken ct)
    {
        if (!TryResolveUser(out var userId, out var error)) return error;

        var session = await _chat.GetForUserAsync(userId, ct);
        if (session is not null)
            await _runner.InterruptAsync(session.Id);

        var ended = await _chat.EndAsync(userId, ct);
        return ended ? NoContent() : NotFound();
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
}
