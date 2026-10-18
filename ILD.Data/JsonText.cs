using System.Text.Json.Nodes;

namespace ILD.Data;

public static class JsonText
{
    /// <summary>The node's text when it is a JSON string; null for anything else.</summary>
    public static string? Of(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
