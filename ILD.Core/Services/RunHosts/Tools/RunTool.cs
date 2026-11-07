using System.Text.RegularExpressions;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// A toolchain a run needs, as mise names it: <c>node@24</c>, <c>dotnet@10</c>, <c>python@3.12</c>. The version is what
/// was asked for, a prefix mise resolves to the newest release it matches when it installs it.
/// </summary>
public sealed partial record RunTool(string Name, string Version)
{
    public override string ToString() => $"{Name}@{Version}";

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
