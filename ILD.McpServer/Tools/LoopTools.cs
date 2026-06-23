using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for the loop the user has open in the Loop Editor (loop editor
/// context, ADR-0011). Scoped to the chat session via the
/// <c>X-ILD-Chat-Session-Id</c> header that <see cref="IldClient"/> sends; the
/// browser stashes the live document on every chat message, so these always act
/// on the loop as-of the current turn.
///
/// <para>
/// <c>update_current_loop</c> is a full-document replacement applied to transient
/// client state only — there is no persist tool. The sole write to a stored loop
/// template stays the editor's human-only Save.
/// </para>
///
/// Drift warning: these names and shapes must stay in lockstep with the Pi
/// surface (<see cref="ILD.Data.ToolDescriptors"/>) and the agent-API endpoints
/// (<c>AgentController</c>) so the chat behaves the same whichever CLI backs it.
///
/// <para>
/// Error surfacing: the MCP SDK masks any exception thrown inside a tool method as
/// a generic "An error occurred invoking '…'" with no detail, which makes a failed
/// read/write impossible to diagnose (route missing on a stale build, API
/// unreachable, etc.). So these tools catch the transport failure and <b>return</b>
/// the reason as text, so the agent — and the human reading the transcript — see
/// the actual cause instead of an opaque invoke error.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LoopTools
{
    private readonly IldClient _ild;

    public LoopTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_current_loop")]
    [Description("Read the loop the user currently has open in the Loop Editor as an ild-loop-template/v1 JSON document (its live, possibly-unsaved nodes and edges). Returns {\"loopEditorOpen\": false} when no loop editor is open. Token cost is paid only when this tool is called.")]
    public async Task<string> GetCurrentLoop()
    {
        try
        {
            return await _ild.GetRawAsync("api/v1/agent/current-loop");
        }
        catch (HttpRequestException ex)
        {
            return Failure("read the current loop", ex);
        }
    }

    [McpServerTool(Name = "update_current_loop")]
    [Description("Replace the loop open in the Loop Editor with a complete ild-loop-template/v1 document (full replacement, NOT a patch — include every node and edge). The open editor validates it and, on success, updates the live canvas immediately; on a validation error the loop is left untouched. This edits transient client state only — it never saves, and you get no structured ack. To check whether your edit applied, call get_current_loop again on a later turn.")]
    public async Task<string> UpdateCurrentLoop(
        [Description("A complete ild-loop-template/v1 document as JSON (with $schema, name, description, recoveryPolicy, nodes, edges).")] string document)
    {
        try
        {
            return await _ild.PutJsonAsync("api/v1/agent/current-loop", new { document });
        }
        catch (HttpRequestException ex)
        {
            return Failure("apply the loop edit", ex);
        }
    }

    /// <summary>
    /// Render a transport failure as a returnable tool result. Returning (not
    /// throwing) is deliberate: a thrown exception is masked by the SDK as a
    /// detail-free generic error, whereas this text reaches the model verbatim.
    /// </summary>
    private static string Failure(string action, HttpRequestException ex)
        => $"[ild-error] Could not {action}: {ex.Message}";
}
