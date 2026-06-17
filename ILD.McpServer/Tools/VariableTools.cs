using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for reading and writing loop variables — named string values
/// scoped to the current loop run (resolved from the ILD_LOOP_RUN_ID env var).
///
/// Loop variables are the hand-off mechanism between nodes: one AI writes a
/// value (e.g. a handoff summary or a changelog) and a later node reads it,
/// either via these tools or via the <c>{{Var.&lt;name&gt;}}</c> template
/// placeholder. Variables live as long as the run.
/// </summary>
[McpServerToolType]
public sealed class VariableTools
{
    private readonly IldClient _ild;

    public VariableTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_loop_variables")]
    [Description("List all loop variables set on the current loop run. Returns an array of {name, value, updatedAt}. Use this to read hand-off values written by an earlier node — e.g. a summary or changelog produced upstream.")]
    public Task<string> GetLoopVariables()
        => _ild.GetRawAsync("api/v1/agent/variables");

    [McpServerTool(Name = "set_loop_variable")]
    [Description("Create or overwrite a loop variable on the current loop run. The name must start with a letter and contain only letters, digits, and underscores. Once set, the value is available to downstream templates as {{Var.<name>}} and to other nodes via get_loop_variables. Use this to hand off text (e.g. a handoff note, summary, or changelog) to a later AI or to the PR node.")]
    public Task<string> SetLoopVariable(
        [Description("Variable name: starts with a letter, then letters/digits/underscores (e.g. handoff, pr_summary).")] string name,
        [Description("Value to store (up to 8192 chars).")] string value)
        => _ild.PutJsonAsync($"api/v1/agent/variables/{Uri.EscapeDataString(name)}", new { value });
}
