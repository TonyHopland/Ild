using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// The MCP tool for suggesting the answer to a work item parked on a question at
/// a Human node. Nothing is sent to the run: a human approves or rejects the
/// suggestion in the work item's Action tab (ADR-0030).
/// </summary>
[McpServerToolType]
public sealed class HumanInputProposalTools
{
    private readonly IldClient _ild;

    public HumanInputProposalTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "propose_human_input")]
    [Description("Suggest the answer to a work item that is waiting for human input at a Human node: the output to take and the optional text to send with it. Nothing is sent until a human approves the suggestion in the work item's Action tab; they may instead reject it. The output is OnSuccess (the Approve button), OnFailure (the Reject button) or the name of one of the node's named outputs; an output the node does not offer is refused with the list it does. A work item that is not waiting at a Human node is refused. A new suggestion for the same work item replaces this session's earlier pending one. Returns the suggestion id and its status (Pending).")]
    public Task<string> ProposeHumanInput(
        [Description("The work item waiting for human input.")] string workItemId,
        [Description("The output to take: OnSuccess, OnFailure or a named output of the Human node.")] string output,
        [Description("The input or context to send with the output (up to 8192 chars). May be empty.")] string text,
        [Description("Why this is the answer (up to 2000 chars), shown to the human deciding.")] string rationale)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/human-input-proposals",
            new { output, text, rationale });
}
