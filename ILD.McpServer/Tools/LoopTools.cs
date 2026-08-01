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
/// Prefer the targeted edits (<c>get_loop_node</c> + <c>edit_loop_node_field</c> for
/// a prompt tweak, <c>edit_loop_file</c> for a structural nudge) over
/// <c>update_current_loop</c>: they change only what you name, so they cannot corrupt
/// an unrelated node, and every one returns a synchronous ack
/// (<c>{ applied, matchCount, validationErrors }</c>) so you learn immediately whether
/// the edit landed. String-replace edits require a UNIQUE match — zero or multiple
/// occurrences change nothing and tell you why. All edits are transient client state
/// only — there is no persist tool; the sole write to a stored loop template stays
/// the editor's human-only Save.
/// </para>
///
/// Drift warning: these names and shapes must stay in lockstep with the Pi
/// surface (<see cref="ILD.Data.ToolDescriptors"/>) and the agent-API endpoints
/// (<c>AgentController</c>) so the chat behaves the same whichever CLI backs it.
/// </summary>
[McpServerToolType]
public sealed class LoopTools
{
    private readonly IldClient _ild;

    public LoopTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_loop_authoring_guide")]
    [Description("Read the loop authoring guide: the node/edge vocabulary, the config field semantics you cannot infer from the name, the graph rules a human's save enforces, and the authoring practices. Call this before writing or restructuring a loop, and again whenever you are unsure of a rule — the chat sends it once per session, so this is how you get it back. Static text; it does not depend on which loop is open.")]
    public Task<string> GetLoopAuthoringGuide()
        => _ild.GetRawAsync("api/v1/agent/loop-authoring-guide");

    [McpServerTool(Name = "get_current_loop")]
    [Description("Read the loop the user currently has open in the Loop Editor as an ild-loop-template/v1 JSON document (its live, possibly-unsaved nodes and edges). Returns {\"loopEditorOpen\": false} when no loop editor is open. Token cost is paid only when this tool is called.")]
    public Task<string> GetCurrentLoop()
        => _ild.GetRawAsync("api/v1/agent/current-loop");

    [McpServerTool(Name = "get_loop_node")]
    [Description("Read a single node from the loop open in the Loop Editor by its id. Returns { id, type, label, config } as JSON with the config's prompt/text fields decoded (plain text, not escaped) — read this first, then craft a unique old_string for edit_loop_node_field. Returns {\"loopEditorOpen\": false} when no loop editor is open, or a 404 error when no node has that id.")]
    public Task<string> GetLoopNode(
        [Description("The id of the node to read (from get_current_loop).")] string nodeId)
        => _ild.GetRawAsync($"api/v1/agent/current-loop/nodes/{Uri.EscapeDataString(nodeId)}");

    [McpServerTool(Name = "edit_loop_node_field")]
    [Description("Targeted find-and-replace on ONE node config field's decoded text (the primary way to tweak a prompt). Give old_string as the plain text to replace — you never handle JSON escaping, the server re-encodes. old_string must match EXACTLY ONCE: zero matches or more than one match change nothing and report the count. Returns { applied, matchCount, validationErrors }; if the resulting graph is invalid the edit is rejected and the canvas is left untouched. On success the live canvas updates immediately (still transient — human Save persists).")]
    public Task<string> EditLoopNodeField(
        [Description("The id of the node to edit.")] string nodeId,
        [Description("The config field to edit, e.g. 'prompt', 'command', 'prDescriptionTemplate', 'output'.")] string field,
        [Description("The decoded text to find. Must occur exactly once in the field.")] string oldString,
        [Description("The decoded replacement text.")] string newString)
        => _ild.PostJsonAsync(
            $"api/v1/agent/current-loop/nodes/{Uri.EscapeDataString(nodeId)}/edit-field",
            new { field, oldString, newString });

    [McpServerTool(Name = "set_loop_node_field")]
    [Description("Overwrite ONE node config field wholesale with a new value (the intentional replace-all path — use this instead of edit_loop_node_field when you want to replace the whole field, e.g. a fresh prompt). The field is created if it does not exist. Returns { applied, matchCount, validationErrors }; a resulting invalid graph is rejected and the canvas is left untouched. Transient client state only.")]
    public Task<string> SetLoopNodeField(
        [Description("The id of the node to edit.")] string nodeId,
        [Description("The config field to overwrite, e.g. 'prompt', 'command'.")] string field,
        [Description("The new value stored as the field's text.")] string value)
        => _ild.PostJsonAsync(
            $"api/v1/agent/current-loop/nodes/{Uri.EscapeDataString(nodeId)}/set-field",
            new { field, value });

    [McpServerTool(Name = "get_loop_file")]
    [Description("Read the whole loop open in the Loop Editor as raw ild-loop-template/v1 JSON text — the exact bytes to target with edit_loop_file. Returns {\"loopEditorOpen\": false} when no loop editor is open. For a single node prefer get_loop_node (decoded, cheaper).")]
    public Task<string> GetLoopFile()
        => _ild.GetRawAsync("api/v1/agent/current-loop/file");

    [McpServerTool(Name = "edit_loop_file")]
    [Description("Targeted find-and-replace on the RAW JSON of the whole loop document — the escape hatch for structural nudges (edges, ids, adding a node) that a field edit can't reach. You are editing raw JSON here, so old_string must include correct JSON escaping (call get_loop_file first). old_string must match EXACTLY ONCE. Returns { applied, matchCount, validationErrors }; an edit that produces invalid JSON or an invalid graph is rejected and the canvas is left untouched. Prefer edit_loop_node_field for prompt text.")]
    public Task<string> EditLoopFile(
        [Description("The raw-JSON text to find. Must occur exactly once in the document.")] string oldString,
        [Description("The raw-JSON replacement text.")] string newString)
        => _ild.PostJsonAsync("api/v1/agent/current-loop/file/edit", new { oldString, newString });

    [McpServerTool(Name = "update_current_loop")]
    [Description("Replace the loop open in the Loop Editor with a complete ild-loop-template/v1 document (full replacement, NOT a patch — include every node and edge). ESCAPE HATCH: prefer the targeted edits (edit_loop_node_field / edit_loop_file), which cannot corrupt unrelated nodes. The server validates the document and returns a synchronous ack { applied, matchCount, validationErrors }; on a validation error the edit is rejected and the loop is left untouched. On success the live canvas updates immediately. Transient client state only — it never saves.")]
    public Task<string> UpdateCurrentLoop(
        [Description("A complete ild-loop-template/v1 document as JSON (with $schema, name, description, recoveryPolicy, nodes, edges).")] string document)
        => _ild.PutJsonAsync("api/v1/agent/current-loop", new { document });
}
