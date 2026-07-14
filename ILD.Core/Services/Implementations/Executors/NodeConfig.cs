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

        /// <summary>
        /// Optional source placeholder to <em>fork</em> from. When set, the node
        /// re-seeds a copy of the session currently bound to this placeholder on
        /// every execution, continues on the copy, and binds the copy to
        /// <see cref="SessionPlaceholder"/> (a throwaway copy when that is unset).
        /// The source session is never written to. When the source has no bound
        /// session the node behaves like a normal new AI node and starts fresh.
        /// </summary>
        public string? ForkFromPlaceholder { get; init; }
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

    /// <summary>
    /// One case of a Condition switch: a predicate that, when it holds, routes
    /// the node to the custom edge named <see cref="EdgeName"/>. Cases are
    /// evaluated in order and the first match wins; if none match the node takes
    /// the switch's default edge. The predicate is picked by <see cref="Variant"/>
    /// exactly as the legacy single-predicate Condition did.
    /// </summary>
    public sealed record ConditionCase
    {
        /// <summary>Which predicate to evaluate: TextMatches | PrExists | HasTag.</summary>
        public string? Variant { get; init; }

        /// <summary>TextMatches: the templated text to test (default <c>{{Node.Input}}</c>).</summary>
        public string? Subject { get; init; }

        /// <summary>TextMatches: the case-insensitive regex matched against the rendered subject.</summary>
        public string? Pattern { get; init; }

        /// <summary>HasTag: the work-item tag tested by case-insensitive whole-string equality.</summary>
        public string? Tag { get; init; }

        /// <summary>The custom edge this case routes to when its predicate holds.</summary>
        public string? EdgeName { get; init; }
    }

    /// <summary>
    /// A Condition node is a switch: an ordered list of <see cref="Cases"/> each
    /// routing to a named custom edge, plus a <see cref="DefaultEdge"/> taken
    /// when no case matches. It never invokes AI, runs a command, or touches the
    /// worktree. Pre-switch true/false conditions are upgraded to this shape by
    /// the one-time <c>ConditionSwitchMigrator</c> at startup; nothing reads the
    /// old top-level predicate keys at runtime.
    /// </summary>
    public sealed record Condition
    {
        /// <summary>Ordered switch cases; the first whose predicate holds wins.</summary>
        public List<ConditionCase>? Cases { get; init; }

        /// <summary>The custom edge taken when no case matches.</summary>
        public string? DefaultEdge { get; init; }

        /// <summary>
        /// Templated output emitted identically on every branch (default
        /// <c>{{Node.Input}}</c>, a pass-through of the incoming node input).
        /// </summary>
        public string? Output { get; init; }
    }
}
