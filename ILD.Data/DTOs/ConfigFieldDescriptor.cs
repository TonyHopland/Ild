namespace ILD.Data.DTOs;

public enum ConfigFieldType
{
    Text,
    Number,
    Toggle,
    Textarea,
    Select,
}

public record ConfigFieldDescriptor(
    string Name,
    ConfigFieldType Type,
    string Label,
    bool Required,
    object? DefaultValue,
    string? Description,
    string[]? Options = null
);
