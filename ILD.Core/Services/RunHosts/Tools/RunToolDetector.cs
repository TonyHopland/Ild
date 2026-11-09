using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// The toolchains a run gets, and where each came from, for the log and the run's events. <see cref="Reasons"/> is
/// parallel to <see cref="Tools"/>. A mise core tool goes in the store every run shares; any other goes in a folder of
/// its repository alone (docs/adr/0035-run-toolchains.md). <see cref="IgnoreScripts"/> is the repository's <c>run.ignoreScripts</c>.
/// </summary>
public sealed record RunToolPlan(IReadOnlyList<RunTool> Tools, IReadOnlyList<string> Reasons, bool IgnoreScripts = false)
{
    public static readonly RunToolPlan Empty = new([], []);

    /// <summary>The core tools: the shared toolchain store holds them.</summary>
    public IReadOnlyList<RunTool> Toolchains => [.. Tools.Where(t => t.IsCore)];

    /// <summary>Every other tool: only its repository's own folder holds them.</summary>
    public IReadOnlyList<RunTool> RepositoryTools => [.. Tools.Where(t => !t.IsCore)];

    public override string ToString() => Tools.Count == 0
        ? "no toolchains"
        : string.Join(", ", Tools.Select((t, i) => $"{(i < Reasons.Count ? Reasons[i] : t.ToString())} -> {(t.IsCore ? "toolchains" : "repository tools")}"));
}

/// <summary>
/// Picks the toolchains a run needs from the repository it works on, read from ILD's own checkout of the base branch
/// before the run exists, so what the agent changes on its branch cannot change what it gets.
/// <list type="number">
/// <item><c>ild.config.json</c>'s <c>run.tools</c> names tools outright, and wins over anything found for the same
/// tool; <c>run.detect: false</c> turns finding off, so those are all the run gets.</item>
/// <item>A project that pins its tools for mise (<c>mise.toml</c>, <c>.mise.toml</c>, <c>.tool-versions</c>) gets those.</item>
/// <item>Otherwise each toolchain whose marker file is at the top of the repository, or one or two folders down, with the
/// version its own version file pins: <c>package.json</c> and <c>.nvmrc</c>, <c>global.json</c> or a project's target
/// framework, <c>.python-version</c>, <c>go.mod</c>, <c>rust-toolchain.toml</c>.</item>
/// </list>
/// </summary>
public static partial class RunToolDetector
{
    public const string ConfigFileName = "ild.config.json";

    /// <summary>Node's version when a project does not pin one: the newest LTS line that still ships corepack.</summary>
    public const string DefaultNodeVersion = "24";

    private static readonly string[] SkippedFolders = ["node_modules", "bin", "obj", "dist", "build", "target", "vendor"];

    public static RunToolPlan Resolve(string repositoryRoot)
    {
        if (!Directory.Exists(repositoryRoot)) return RunToolPlan.Empty;

        var (configured, detect, ignoreScripts) = ReadConfig(repositoryRoot);
        var found = new Dictionary<string, (RunTool Tool, string Reason)>(StringComparer.Ordinal);
        if (detect)
        {
            foreach (var hit in FromMiseFiles(repositoryRoot).Concat(FromMarkers(repositoryRoot)))
                found.TryAdd(hit.Tool.Name, hit);
        }
        foreach (var tool in configured)
            found[tool.Name] = (tool, $"{tool} from {ConfigFileName}");

        var ordered = found.Values.OrderBy(f => f.Tool.Name, StringComparer.Ordinal).ToList();
        return new RunToolPlan(ordered.Select(f => f.Tool).ToList(), ordered.Select(f => f.Reason).ToList(), ignoreScripts);
    }

    // -- ild.config.json ---------------------------------------------------------

    /// <summary><c>run.tools</c> as a list (<c>["node@24"]</c>) or a map (<c>{"node": "24"}</c>), <c>run.detect</c>, and <c>run.ignoreScripts</c>.</summary>
    internal static (IReadOnlyList<RunTool> Tools, bool Detect, bool IgnoreScripts) ReadConfig(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, ConfigFileName);
        if (!File.Exists(path)) return ([], true, false);
        JsonNode? run;
        try
        {
            run = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })?["run"];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], true, false);
        }
        if (run is not JsonObject section) return ([], true, false);

        var detect = section["detect"] is not JsonValue flag || !flag.TryGetValue<bool>(out var value) || value;
        var ignoreScripts = section["ignoreScripts"] is JsonValue ignore && ignore.TryGetValue<bool>(out var ignored) && ignored;
        var tools = new List<RunTool>();
        switch (section["tools"])
        {
            case JsonArray list:
                foreach (var item in list)
                    if (item is JsonValue v && v.TryGetValue<string>(out var spec) && RunTool.Parse(spec) is { } tool)
                        tools.Add(tool);
                break;
            case JsonObject map:
                foreach (var (name, version) in map)
                    if (RunTool.Create(name, version is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) is { } tool)
                        tools.Add(tool);
                break;
        }
        return (tools, detect, ignoreScripts);
    }

    // -- mise's own files --------------------------------------------------------

    private static IEnumerable<(RunTool Tool, string Reason)> FromMiseFiles(string root)
    {
        foreach (var name in new[] { "mise.toml", ".mise.toml", Path.Combine(".config", "mise.toml") })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            foreach (var tool in ParseMiseToml(Lines(path)))
                yield return (tool, $"{tool} from {name.Replace('\\', '/')}");
        }

        var toolVersions = Path.Combine(root, ".tool-versions");
        if (File.Exists(toolVersions))
        {
            foreach (var line in Lines(toolVersions))
            {
                var parts = StripComment(line).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && RunTool.Create(parts[0], parts[1]) is { } tool)
                    yield return (tool, $"{tool} from .tool-versions");
            }
        }
    }

    /// <summary>The <c>[tools]</c> table: <c>node = "24"</c>, <c>python = ["3.12", "3.11"]</c> (the first), <c>go = { version = "1.23" }</c>.</summary>
    internal static IEnumerable<RunTool> ParseMiseToml(IEnumerable<string> lines)
    {
        var inTools = false;
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.StartsWith('['))
            {
                inTools = line == "[tools]";
                continue;
            }
            if (!inTools || TomlEntry().Match(line) is not { Success: true } entry) continue;
            var name = entry.Groups["name"].Value.Trim('"', '\'');
            var value = entry.Groups["value"].Value;
            var version = TomlFirstString().Match(value) is { Success: true } s ? s.Groups[1].Value : null;
            if (RunTool.Create(name, version) is { } tool) yield return tool;
        }
    }

    // -- Marker files ------------------------------------------------------------

    private static IEnumerable<(RunTool Tool, string Reason)> FromMarkers(string root)
    {
        // The top, then each folder one and two down (src/App/App.csproj, packages/web/package.json), nearest first.
        var children = Subfolders(root);
        var folders = new List<string> { root };
        folders.AddRange(children);
        folders.AddRange(children.SelectMany(Subfolders));

        string? First(params string[] patterns)
        {
            foreach (var folder in folders)
                foreach (var pattern in patterns)
                {
                    var hit = SafeFiles(folder, pattern).FirstOrDefault();
                    if (hit is not null) return hit;
                }
            return null;
        }
        string Shown(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        if (First("package.json") is { } package)
        {
            var version = PinnedNode(root, Path.GetDirectoryName(package)!, out var from) ?? DefaultNodeVersion;
            yield return (new RunTool("node", version), $"node@{version} for {Shown(package)}{(from is null ? "" : $" ({from})")}");
        }
        if (First("bun.lock", "bun.lockb") is { } bun)
            yield return (new RunTool("bun", "latest"), $"bun@latest for {Shown(bun)}");

        if (DotnetFor(root, folders) is { } dotnet)
            yield return dotnet;

        if (First("pyproject.toml", "requirements.txt", "uv.lock", "setup.py", "Pipfile") is { } python)
        {
            var version = FirstLineOf(Path.Combine(root, ".python-version")) ?? "3";
            if (RunTool.Create("python", version) is { } tool) yield return (tool, $"{tool} for {Shown(python)}");
        }

        if (First("go.mod") is { } goMod)
        {
            var version = GoVersion(Lines(goMod)) ?? "latest";
            if (RunTool.Create("go", version) is { } tool) yield return (tool, $"{tool} for {Shown(goMod)}");
        }

        if (First("Cargo.toml") is { } cargo)
        {
            var version = RustChannel(root) ?? "stable";
            if (RunTool.Create("rust", version) is { } tool) yield return (tool, $"{tool} for {Shown(cargo)}");
        }

        if (First("pom.xml", "build.gradle", "build.gradle.kts") is { } java)
        {
            var version = FirstLineOf(Path.Combine(root, ".java-version")) ?? "21";
            if (RunTool.Create("java", version) is { } tool) yield return (tool, $"{tool} for {Shown(java)}");
        }
    }

    /// <summary><c>.nvmrc</c> or <c>.node-version</c> next to the package, else at the top: <c>v20.11.0</c> → 20.11.0, <c>lts/*</c> → lts.</summary>
    private static string? PinnedNode(string root, string packageFolder, out string? from)
    {
        foreach (var folder in new[] { packageFolder, root }.Distinct())
            foreach (var name in new[] { ".nvmrc", ".node-version" })
                if (FirstLineOf(Path.Combine(folder, name)) is { } pinned)
                {
                    from = Path.GetRelativePath(root, Path.Combine(folder, name)).Replace('\\', '/');
                    var version = pinned.TrimStart('v', 'V');
                    if (version.StartsWith("lts", StringComparison.OrdinalIgnoreCase)) version = "lts";
                    else if (version is "node" or "latest" or "current") version = "latest";
                    return RunTool.Create("node", version)?.Version;
                }
        from = null;
        return null;
    }

    /// <summary>
    /// .NET: <c>global.json</c>'s SDK version when there is one, else the newest major of the target frameworks the
    /// projects name (<c>net10.0</c> → 10), for a solution or project file at the top or one or two folders down.
    /// </summary>
    private static (RunTool Tool, string Reason)? DotnetFor(string root, IReadOnlyList<string> folders)
    {
        var markers = folders.SelectMany(f => new[] { "*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj" }.SelectMany(p => SafeFiles(f, p))).ToList();
        var globalJson = Path.Combine(root, "global.json");
        if (markers.Count == 0 && !File.Exists(globalJson)) return null;

        if (File.Exists(globalJson))
        {
            try
            {
                var sdk = JsonNode.Parse(File.ReadAllText(globalJson), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["sdk"]?["version"];
                if (sdk is JsonValue v && v.TryGetValue<string>(out var pinned) && RunTool.Create("dotnet", pinned) is { } tool)
                    return (tool, $"{tool} from global.json");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }

        var projectFiles = folders
            .SelectMany(f => new[] { "*.csproj", "*.fsproj", "*.vbproj", "Directory.Build.props" }.SelectMany(p => SafeFiles(f, p)))
            .ToList();
        var majors = projectFiles
            .SelectMany(file => TargetFramework().Matches(SafeRead(file)))
            .SelectMany(element => NetMajor().Matches(element.Groups[1].Value).Select(m => int.Parse(m.Groups[1].Value)))
            .ToList();
        var version = majors.Count > 0 ? majors.Max().ToString() : "latest";
        var shown = Path.GetRelativePath(root, markers.FirstOrDefault() ?? globalJson).Replace('\\', '/');
        return (new RunTool("dotnet", version), $"dotnet@{version} for {shown}");
    }

    /// <summary><c>toolchain go1.23.4</c> wins over <c>go 1.23</c>.</summary>
    internal static string? GoVersion(IEnumerable<string> goMod)
    {
        string? go = null;
        foreach (var raw in goMod)
        {
            var line = raw.Trim();
            if (line.StartsWith("toolchain go", StringComparison.Ordinal))
                return RunTool.Create("go", line["toolchain go".Length..].Trim())?.Version;
            if (line.StartsWith("go ", StringComparison.Ordinal))
                go = RunTool.Create("go", line[3..].Trim())?.Version;
        }
        return go;
    }

    private static string? RustChannel(string root)
    {
        var toml = Path.Combine(root, "rust-toolchain.toml");
        if (File.Exists(toml))
            foreach (var line in Lines(toml))
                if (RustChannelLine().Match(line) is { Success: true } m)
                    return RunTool.Create("rust", m.Groups[1].Value)?.Version;
        return FirstLineOf(Path.Combine(root, "rust-toolchain")) is { } plain ? RunTool.Create("rust", plain)?.Version : null;
    }

    // -- Files -------------------------------------------------------------------

    private static List<string> Subfolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(d => !Path.GetFileName(d).StartsWith('.') && !SkippedFolders.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try { return Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string SafeRead(string path)
    {
        try { return new FileInfo(path).Length > 1024 * 1024 ? string.Empty : File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static IEnumerable<string> Lines(string path) => SafeRead(path).Split('\n');

    private static string? FirstLineOf(string path)
        => File.Exists(path)
            ? Lines(path).Select(l => StripComment(l).Trim()).FirstOrDefault(l => l.Length > 0)
            : null;

    private static string StripComment(string line) => line.Split('#', 2)[0];

    [GeneratedRegex(@"^(?<name>""[^""]+""|'[^']+'|[A-Za-z0-9._:/@-]+)\s*=\s*(?<value>.+)$")]
    private static partial Regex TomlEntry();

    [GeneratedRegex(@"[""']([^""']+)[""']")]
    private static partial Regex TomlFirstString();

    [GeneratedRegex(@"<TargetFrameworks?>([^<]*)</")]
    private static partial Regex TargetFramework();

    // net10.0, net8.0-windows; not netstandard2.0 or netcoreapp3.1.
    [GeneratedRegex(@"(?<![a-z])net(\d+)\.\d")]
    private static partial Regex NetMajor();

    [GeneratedRegex(@"^\s*channel\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex RustChannelLine();
}
