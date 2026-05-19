namespace ILD.Data.DTOs;

public sealed record AiToolDefinition(
    string Key,
    string Label,
    string Description,
    bool DefaultEnabled = true);