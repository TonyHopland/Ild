using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for proposing changes to saved loop templates. Templates are
/// human-owned: an agent reads one with get_loop_template, proposes a whole new
/// document or an Edit List against the version it read, and a human saves,
/// rejects or reviews the proposal (ADR-0028). No tool saves, rejects or applies anything.
/// </summary>
[McpServerToolType]
public sealed class LoopTemplateProposalTools
{
    private readonly IldClient _ild;

    public LoopTemplateProposalTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "propose_loop_template_change")]
    [Description("Propose a change to a saved loop template. Nothing changes until a human saves the proposal as a new version (as is, or after editing it in the Loop Editor); they may instead reject it, with a reason. Read the template with get_loop_template first, then send EITHER edits OR document, never both. "
        + "Prefer edits for most changes: an ordered list of the Loop Editor's own edits (editNodeField, setNodeField, editFile), applied in order to the template's latest version, all or nothing. Send a whole document only for a rewrite. "
        + "editNodeField finds and replaces in one node's decoded text field: put prompt text changes here, since you never handle JSON escaping. setNodeField replaces a whole field (a JSON array as text for outputs, matchRules, cases and toolAllowlist). editFile finds and replaces in the document JSON text exactly as get_loop_template returned it (compact, with characters such as ' < > & + and non-ASCII escaped as \\uXXXX), even after earlier edits in the list: use it for structure, such as adding a node or an edge. Each oldString must match exactly once. "
        + "Only the final document must be a valid graph: a step in between may break it (declare a node, then wire it, then route it). If an edit does not land, nothing is stored and the error names it as edits[i] (with editIndex i) and says why; fix that edit and propose again. "
        + "A document is the COMPLETE ild-loop-template/v2 document, keeping the node ids of the nodes you keep. "
        + "baseVersion is the version get_loop_template returned, and it must be the template's LATEST version; a proposal against an older one is refused with the latest version number, so read it again and redo your change. The resulting document is checked the way a Loop Editor save is, and every error is returned at once for you to fix now. You cannot propose a new template or deleting one. If the template gets a newer version before a human decides, the proposal goes Stale and can no longer be saved; propose again against the latest version. A new proposal for the same template replaces this session's earlier pending one. Returns the proposal id, the template id, the base version and its status (Pending). Use list_loop_template_proposals to see what became of it.")]
    public Task<string> ProposeLoopTemplateChange(
        [Description("Loop template GUID, or its exact name (case-insensitive; archived templates included), as get_loop_template takes it.")] string nameOrId,
        [Description("The version get_loop_template returned with the document; must be the template's latest version.")] int baseVersion,
        [Description("Why the loop should change this way (up to 2000 chars), shown to the human deciding.")] string rationale,
        [Description("The edits to make, in order, to the latest version (an Edit List). Use this for most changes; omit document.")] LoopTemplateEdit[]? edits = null,
        [Description("The complete proposed ild-loop-template/v2 document, as JSON text, for a rewrite only; omit edits.")] string? document = null)
        => _ild.PostJsonAsync("api/v1/agent/loop-templates/proposals", new { nameOrId, baseVersion, document, edits, rationale });

    [McpServerTool(Name = "list_loop_template_proposals")]
    [Description("List the loop template proposals THIS session made, newest first: each one's template, base version, rationale, how many edits an Edit List proposal had (editCount; null for a whole document), status (Pending, Applied, Rejected, Stale or Superseded), whether an Applied one was saved as proposed or edited first (editedBeforeSave) and the version it became (appliedVersion), and a rejection's reason. Stale means the template got a newer version first and nothing was saved. Read-only — only a human can save or reject a proposal.")]
    public Task<string> ListLoopTemplateProposals()
        => _ild.GetRawAsync("api/v1/agent/loop-templates/proposals");
}

/// <summary>
/// One edit of an Edit List: a Loop Editor edit named by <see cref="Op"/>, with that edit's own members.
/// The server reads it as <c>ILD.Data.DTOs.LoopTemplateEdit</c>, which has the same members.
/// </summary>
public sealed class LoopTemplateEdit
{
    [Description("editNodeField, setNodeField or editFile.")]
    public string? Op { get; init; }

    [Description("The node's id (editNodeField, setNodeField).")]
    public string? NodeId { get; init; }

    [Description("The config field, e.g. prompt or outputs (editNodeField, setNodeField).")]
    public string? Field { get; init; }

    [Description("The exact text to replace; must match exactly once (editNodeField: in the field's decoded text; editFile: in the document JSON as get_loop_template returned it).")]
    public string? OldString { get; init; }

    [Description("The replacement text (editNodeField, editFile).")]
    public string? NewString { get; init; }

    [Description("The field's whole new value as text; a JSON array as text for outputs, matchRules, cases and toolAllowlist (setNodeField).")]
    public string? Value { get; init; }
}
