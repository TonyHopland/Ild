namespace ILD.Data.DTOs;

/// <summary>
/// Describes a single tool exposed by the ILD agent-scoped API.
/// Used as the single source of truth for the Pi extension generator.
/// </summary>
public sealed class ToolDescriptor
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string EndpointPath { get; init; } = "";
    public HttpMethod HttpMethod { get; init; } = HttpMethod.Get;
    public ToolParameterDescriptor[] Parameters { get; init; } = [];
}

/// <summary>
/// Describes a single parameter of a tool.
/// </summary>
public sealed class ToolParameterDescriptor
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string TsType { get; init; } = ""; // e.g. "string", "number", "boolean", "string[]"
    public bool IsOptional { get; init; }
    public bool IsBodyParam { get; init; } // true for POST body fields, false for query params
}
