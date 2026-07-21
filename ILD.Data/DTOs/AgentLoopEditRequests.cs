namespace ILD.Data.DTOs;

/// <summary>
/// Request body for a targeted node-field string replace
/// (<c>POST /api/v1/agent/current-loop/nodes/{nodeId}/edit-field</c>). The strings
/// are the node field's <em>decoded</em> text — the server owns the JSON escaping
/// (loop editor context, ADR-0011).
/// </summary>
public class AgentLoopNodeFieldEditRequest
{
    /// <summary>The config field name to edit (e.g. <c>prompt</c>, <c>command</c>).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The decoded substring to replace. Must match exactly once.</summary>
    public string OldString { get; set; } = string.Empty;

    /// <summary>The decoded replacement text.</summary>
    public string NewString { get; set; } = string.Empty;
}

/// <summary>
/// Request body for a wholesale node-field overwrite
/// (<c>POST /api/v1/agent/current-loop/nodes/{nodeId}/set-field</c>).
/// </summary>
public class AgentLoopNodeFieldSetRequest
{
    /// <summary>The config field name to overwrite (created if absent).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The new decoded value stored as the field's text.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Request body for a raw-JSON string replace over the whole loop document
/// (<c>POST /api/v1/agent/current-loop/file/edit</c>). The escape hatch for
/// structural nudges a field edit can't reach.
/// </summary>
public class AgentLoopFileEditRequest
{
    /// <summary>The substring of the raw JSON to replace. Must match exactly once.</summary>
    public string OldString { get; set; } = string.Empty;

    /// <summary>The replacement text.</summary>
    public string NewString { get; set; } = string.Empty;
}
