using System.Text.RegularExpressions;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// A toolchain a run needs, as mise names it: <c>node@24</c>, <c>dotnet@10</c>, <c>python@3.12</c>. The version is what
/// was asked for, a prefix mise resolves to the newest release it matches when it installs it.
/// </summary>
public sealed partial record RunTool(string Name, string Version)
{
    public override string ToString() => $"{Name}@{Version}";

    /// <summary>The tools mise builds into itself: it installs each from the upstream's own release and runs no code a repository chose.</summary>
    private static readonly HashSet<string> CoreNames = new(StringComparer.Ordinal)
    {
        "bun", "deno", "dotnet", "elixir", "erlang", "go", "java", "node", "python", "ruby", "rust", "swift", "zig",
    };

    /// <summary>
    /// Whether this is a mise core tool by its plain name. Every other spec (a backend such as <c>npm:prettier</c>, or a
    /// registry short name such as <c>prettier</c>, which mise resolves to a backend) installs a package whose install
    /// scripts a repository chose, so it never goes in the store every run shares (docs/adr/0035-run-toolchains.md).
    /// </summary>
    public bool IsCore => CoreNames.Contains(Name);

    /// <summary><c>name@version</c>, or a bare name for its newest release; null when either part is not one mise takes.</summary>
    public static RunTool? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var at = spec.IndexOf('@');
        var name = (at < 0 ? spec : spec[..at]).Trim().ToLowerInvariant();
        var version = at < 0 ? "latest" : spec[(at + 1)..].Trim();
        return Create(name, version);
    }

    /// <summary>The tool, or null when either part is not one mise takes. Neither part can carry a shell character.</summary>
    public static RunTool? Create(string name, string? version)
    {
        name = name.Trim().ToLowerInvariant();
        version = string.IsNullOrWhiteSpace(version) ? "latest" : version.Trim();
        return NamePattern().IsMatch(name) && VersionPattern().IsMatch(version) ? new RunTool(name, version) : null;
    }

    // A mise core tool or registry short name (node, dotnet, pnpm), or a backend one (npm:prettier, aqua:cli/cli).
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(:[a-z0-9@][a-z0-9@._/-]*)?$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$")]
    private static partial Regex VersionPattern();
}
