namespace ILD.Data.DTOs;

/// <summary>
/// What an adapter declares about its provider type, so the UI, the providers
/// API, tool handling and chat titles read one answer instead of each keeping
/// its own list of types.
/// </summary>
public sealed record AgentAdapterTraits
{
    /// <summary>The type's name in user-facing text, e.g. "Codex".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// How the CLI signs itself in, for a type that has no Base URL or API key;
    /// null for a type that connects with a Base URL (and API key).
    /// </summary>
    public CliAuthTraits? CliAuth { get; init; }

    /// <summary>The tools a step or chat can turn on for this type, usually <see cref="AiToolCatalog.DefaultTools"/> or <see cref="AiToolCatalog.IldOnlyTools"/>.</summary>
    public required IReadOnlyList<AiToolDefinition> SupportedTools { get; init; }

    /// <summary>
    /// Whether an explicit selection that is empty, or empty once unsupported keys
    /// are dropped, means "no tools" rather than the type's defaults. True where
    /// that is the only way to turn the type's one tool off.
    /// </summary>
    public required bool EmptyToolSelectionMeansNone { get; init; }

    /// <summary>
    /// Whether the CLI can be run with no tools and no MCP servers at all
    /// (<see cref="AgentExecutionContext.NoTools"/>). A caller that needs a
    /// tool-less call, like chat titles, skips a type where this is false.
    /// </summary>
    public required bool SupportsNoTools { get; init; }

    /// <summary>What the "Extra CLI arguments" value may not contain for this type.</summary>
    public required ExtraCliArgsRules ExtraCliArgs { get; init; }

    /// <summary>
    /// The traits of a type no adapter is registered for: no tools, an empty
    /// selection is not "none", nothing to refuse beyond what every provider refuses.
    /// </summary>
    public static AgentAdapterTraits None { get; } = new()
    {
        DisplayName = "",
        SupportedTools = [],
        EmptyToolSelectionMeansNone = false,
        SupportsNoTools = true,
        ExtraCliArgs = ExtraCliArgsRules.None,
    };
}

/// <summary>
/// The CLI's own sign-in, shown in the AI Providers login note.
/// <paramref name="SignInStep"/> is a Markdown fragment using only <c>`code`</c>
/// and <c>**bold**</c>, read after "open a terminal, start the CLI and …".
/// </summary>
public sealed record CliAuthTraits(string CliName, string SignInStep);

/// <summary>
/// The per-type rules for the "Extra CLI arguments" value, beyond the ones every
/// type shares (it must split, no bare <c>--</c>, no <c>--model</c>).
/// </summary>
public sealed record ExtraCliArgsRules
{
    /// <summary>Flags ILD sets for this type, or that would undo what it sets.</summary>
    public required IReadOnlyList<string> ReservedFlags { get; init; }

    /// <summary>Why a reserved flag is refused, where "ILD already sets it for this provider" is not the reason; null when it always is.</summary>
    public IReadOnlyDictionary<string, string>? ReservedFlagReasons { get; init; }

    /// <summary>
    /// For a CLI whose <c>-c</c>/<c>--config key=value</c> overrides any config key:
    /// the first dotted key segments ILD sets itself, with what they set. Null when
    /// the CLI has no such override, so <c>-c</c>/<c>--config</c> are not checked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RefusedConfigKeys { get; init; }

    /// <summary>Whether <c>-m</c> is the CLI's model flag, refused like <c>--model</c>.</summary>
    public required bool ModelHasShortFlag { get; init; }

    /// <summary>
    /// Whether the CLI takes a one-letter flag's value attached (<c>-mgpt-5</c>), so
    /// a token's prefix is checked too. Off elsewhere: other CLIs have free-text
    /// options whose values may well start with "-p" or "-e".
    /// </summary>
    public required bool AttachedShortValues { get; init; }

    /// <summary>No type-specific rules.</summary>
    public static ExtraCliArgsRules None { get; } = new()
    {
        ReservedFlags = [],
        ModelHasShortFlag = false,
        AttachedShortValues = false,
    };
}
