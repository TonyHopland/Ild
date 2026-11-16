namespace ILD.Core.Services.Implementations;

/// <summary>A worktree's <c>ild.config.json</c> as read: whether it defines previews at all, and if so the parsed document.</summary>
internal sealed record LoadedPreviewConfig(bool Configured, string WorktreePath, string ConfigPath, IldWorkspaceConfig? Config, string? Message);

/// <summary>One profile of a worktree's config, selected and validated: what a start is given to run.</summary>
internal sealed record PreviewProfile(LoadedPreviewConfig Loaded, string Name, PreviewProfileConfig Config)
{
    public string ConfigPath => Loaded.ConfigPath;
    public IReadOnlyList<PreviewServiceConfig> Services => Config.Services;
}

internal sealed class IldWorkspaceConfig
{
    public PreviewRootConfig? Preview { get; set; }
}

internal sealed class PreviewRootConfig
{
    public string? DefaultProfile { get; set; }
    public Dictionary<string, PreviewProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PreviewProfileConfig
{
    public List<string>? Volumes { get; set; }
    public List<PreviewCommandConfig> Install { get; set; } = new();
    public List<PreviewServiceConfig> Services { get; set; } = new();
}

internal class PreviewCommandConfig
{
    public string? Cwd { get; set; }
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string>? Env { get; set; }
}

internal sealed class PreviewServiceConfig : PreviewCommandConfig
{
    public string Name { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public int? SuggestedPort { get; set; }
    public string? HealthUrl { get; set; }

    /// <summary>How long to wait for the service to become healthy; the default applies when unset.</summary>
    public int? HealthTimeoutSeconds { get; set; }
    public bool Public { get; set; }
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Whether the preview proxy replaces the <c>Host</c> header with the
    /// loopback authority it forwards to. Defaults to true because dev servers
    /// that validate the host (Vite, webpack-dev-server, Rails, Django) reject a
    /// request arriving as <c>wi-12.ild.example</c> outright. Set false for a
    /// service that needs to see the real browser-facing host — one that builds
    /// absolute links or issues host-bound redirects — and add the preview
    /// wildcard to that service's own allowed-hosts list instead.
    /// </summary>
    public bool RewriteHost { get; set; } = true;
}
