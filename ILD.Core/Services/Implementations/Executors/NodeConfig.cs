using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// One AI output-matching rule: if <see cref="Pattern"/> (a case-insensitive
    /// regex) matches the AI output, route to the custom edge named
    /// <see cref="EdgeName"/>. Rules are evaluated in order; the first match wins.
    /// </summary>
    public sealed record AiMatchRule
    {
        public string? Pattern { get; init; }
        public string? EdgeName { get; init; }
    }

    public sealed record Ai
    {
        public string? AiProviderId { get; init; }
        public bool? UseSession { get; init; }
        public string? Prompt { get; init; }
        public string[]? ToolAllowlist { get; init; }

        /// <summary>
        /// Ordered output-match rules routing to named custom edges. Supersedes
        /// <see cref="RejectPattern"/>.
        /// </summary>
        public List<AiMatchRule>? MatchRules { get; init; }

        /// <summary>
        /// Legacy single reject pattern. Retained so templates authored before
        /// named custom edges keep routing a matching output to the fallback
        /// (OnFailure) edge. Ignored when <see cref="MatchRules"/> is set.
        /// </summary>
        public string? RejectPattern { get; init; }

        public JsonElement? AdapterConfig { get; init; }
        public string? SessionPlaceholder { get; init; }
    }

    public sealed record Human
    {
        public string? Prompt { get; init; }
    }

    public sealed record Prompt
    {
        [JsonPropertyName("prompt")]
        public string? Template { get; init; }
    }

    public sealed record Pr
    {
        public string? Prompt { get; init; }
        public string? PrDescriptionTemplate { get; init; }
        public string? PrCommentTemplate { get; init; }
    }
}
