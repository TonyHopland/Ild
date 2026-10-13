using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for the instance's global variables, which loop prompts read as
/// <c>{{Global.&lt;name&gt;}}</c>. They are human-owned: an agent reads them and
/// suggests a whole new value for one, and a human approves or rejects the
/// suggestion (ADR-0027). No tool decides or deletes anything.
/// </summary>
[McpServerToolType]
public sealed class GlobalVariableTools
{
    private readonly IldClient _ild;

    public GlobalVariableTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_global_variables")]
    [Description("List every global variable on this ILD instance, ordered by name. Returns an array of {name, value, updatedAt}. Loop prompts read these as {{Global.<name>}}. Read-only — use propose_global_variable_change to suggest a new value.")]
    public Task<string> GetGlobalVariables()
        => _ild.GetRawAsync("api/v1/agent/global-variables");

    [McpServerTool(Name = "propose_global_variable_change")]
    [Description("Suggest a new value for a global variable. Nothing changes until a human approves the suggestion; they may instead reject it, with a reason. The value is the COMPLETE new text that replaces the current value — there is no append or patch, so read the current value with get_global_variables and send it whole with your change. A name no variable has suggests creating that variable. You cannot suggest deleting a variable. A value longer than the instance's length limit is refused with its length and the limit; consolidate it and suggest again. If the variable changes before a human approves, the suggestion goes stale and nothing is applied. A new suggestion for the same variable replaces this session's earlier pending one. Returns the suggestion id and its status (Pending). Use list_global_variable_proposals to see what became of it.")]
    public Task<string> ProposeGlobalVariableChange(
        [Description("Variable name: a letter, then letters/digits/underscores (e.g. review_rules). Matched ignoring case.")] string name,
        [Description("The complete new text of the variable. May be empty.")] string value,
        [Description("Why the variable should say this (up to 2000 chars), shown to the human deciding.")] string rationale)
        => _ild.PostJsonAsync("api/v1/agent/global-variables/proposals", new { name, value, rationale });

    [McpServerTool(Name = "list_global_variable_proposals")]
    [Description("List the global variable suggestions THIS session made, newest first: each one's name, suggested value, rationale, status (Pending, Approved, Rejected, Stale or Superseded) and a rejection's reason. Read-only — only a human can approve or reject a suggestion.")]
    public Task<string> ListGlobalVariableProposals()
        => _ild.GetRawAsync("api/v1/agent/global-variables/proposals");
}
