namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped update-current-loop endpoint
/// (<c>PUT /api/v1/agent/current-loop</c>), scoped to the chat session identified
/// by the <c>X-ILD-Chat-Session-Id</c> header (loop editor context, ADR-0011).
/// </summary>
public class AgentLoopUpdateRequest
{
    /// <summary>
    /// A complete <c>ild-loop-template/v1</c> document (full replacement, not a
    /// patch). The server forwards it verbatim to the open Loop Editor, which
    /// validates and direct-applies it to the live canvas.
    /// </summary>
    public string Document { get; set; } = string.Empty;
}
