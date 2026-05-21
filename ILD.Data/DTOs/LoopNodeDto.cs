using System.Text.Json.Serialization;

namespace ILD.Data.DTOs;

public class LoopNodeDto
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string NodeType { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public Dictionary<string, object> Config { get; set; } = new();

    public int? MaxTraversals { get; set; }
}
