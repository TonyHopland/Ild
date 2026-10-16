using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for proposing changes to saved loop templates. Templates are
/// human-owned: an agent reads one with get_loop_template, proposes a whole new
/// document against the version it read, and a human saves, rejects or reviews
/// the proposal (ADR-0028). No tool saves, rejects or applies anything.
/// </summary>
[McpServerToolType]
public sealed class LoopTemplateProposalTools
{
    private readonly IldClient _ild;

    public LoopTemplateProposalTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "propose_loop_template_change")]
    [Description("Propose a change to a saved loop template. Nothing changes until a human saves the proposal as a new version (as is, or after editing it in the Loop Editor); they may instead reject it, with a reason. The document is the COMPLETE ild-loop-template/v2 document — there is no patch format: read the template with get_loop_template, edit that document, and send it whole, keeping the node ids of the nodes you keep. baseVersion is the version get_loop_template returned, and it must be the template's LATEST version; a proposal against an older one is refused with the latest version number, so read it again and redo your change. The document is checked the way a Loop Editor save is, and every error is returned at once for you to fix now. You cannot propose a new template or deleting one. If the template gets a newer version before a human decides, the proposal goes Stale and can no longer be saved; propose again against the latest version. A new proposal for the same template replaces this session's earlier pending one. Returns the proposal id, the template id, the base version and its status (Pending). Use list_loop_template_proposals to see what became of it.")]
    public Task<string> ProposeLoopTemplateChange(
        [Description("Loop template GUID, or its exact name (case-insensitive; archived templates included), as get_loop_template takes it.")] string nameOrId,
        [Description("The version get_loop_template returned with the document; must be the template's latest version.")] int baseVersion,
        [Description("The complete proposed ild-loop-template/v2 document, as JSON text.")] string document,
        [Description("Why the loop should change this way (up to 2000 chars), shown to the human deciding.")] string rationale)
        => _ild.PostJsonAsync("api/v1/agent/loop-templates/proposals", new { nameOrId, baseVersion, document, rationale });

    [McpServerTool(Name = "list_loop_template_proposals")]
    [Description("List the loop template proposals THIS session made, newest first: each one's template, base version, rationale, status (Pending, Applied, Rejected, Stale or Superseded), whether an Applied one was saved as proposed or edited first (editedBeforeSave) and the version it became (appliedVersion), and a rejection's reason. Stale means the template got a newer version first and nothing was saved. Read-only — only a human can save or reject a proposal.")]
    public Task<string> ListLoopTemplateProposals()
        => _ild.GetRawAsync("api/v1/agent/loop-templates/proposals");
}
