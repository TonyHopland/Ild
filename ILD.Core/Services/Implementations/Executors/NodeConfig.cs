using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Typed views over the JSON blob stored on <c>LoopNode.Config</c>. Each node
/// kind has its own record — keeps string-typed lookups out of the executors
/// and the engine, and gives validators / docs a single source of truth.
/// </summary>
internal static class NodeConfig
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static T Parse<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? new T(); }
        catch { return new T(); }
    }

    public sealed record Cmd
    {
        public string? Command { get; init; }
    }

    public sealed record Ai
    {
        public string? AiProviderId { get; init; }
        public string? InitialPrompt { get; init; }
        public string? LoopPrompt { get; init; }
        public string? RejectPattern { get; init; }
        public JsonElement? AdapterConfig { get; init; }
        public string? SessionInput { get; init; }
        public string? SessionOutput { get; init; }
    }

    public sealed record Human
    {
        public string? Prompt { get; init; }
    }

    public sealed record Pr
    {
        public string? Prompt { get; init; }
        public string? PrDescriptionTemplate { get; init; }
    }
}
