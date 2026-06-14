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

    public sealed record Start
    {
        /// <summary>
        /// When true, the Start node runs the default <c>ild.config.json</c>
        /// preview profile's install steps in the freshly prepared worktree
        /// before routing to OnSuccess. A failing install step fails the node.
        /// </summary>
        public bool? RunInstall { get; init; }
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
        /// Ordered output-match rules routing to named custom edges. The first
        /// rule whose pattern matches the output routes to its named edge; no
        /// match takes the default OnSuccess edge.
        /// </summary>
        public List<AiMatchRule>? MatchRules { get; init; }

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
