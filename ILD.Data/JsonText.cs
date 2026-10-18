using System.Text.Json;
using System.Text.Json.Nodes;

namespace ILD.Data;

public static class JsonText
{
    private static readonly JsonDocumentOptions NoDuplicateKeys = new() { AllowDuplicateProperties = false };

    /// <summary>The node's text when it is a JSON string; null for anything else.</summary>
    public static string? Of(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// <paramref name="json"/> parsed, with a duplicate key refused as a <see cref="JsonException"/>
    /// like any other bad JSON: a <see cref="JsonObject"/> holding one throws
    /// <see cref="ArgumentException"/> the first time it is read.
    /// </summary>
    public static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: NoDuplicateKeys);
}
