using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped set-loop-variable endpoint
/// (<c>PUT /api/v1/agent/variables/{name}</c>). The variable is scoped to the
/// run identified by the <c>X-ILD-Run-Id</c> header and becomes available to
/// templates as <c>{{Var.&lt;name&gt;}}</c>.
/// </summary>
public class AgentSetVariableRequest
{
    /// <summary>The new value. Empty is allowed (clears the variable's text).</summary>
    [StringLength(8192)]
    public string Value { get; set; } = string.Empty;
}
