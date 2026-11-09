using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>What a run's commands get from the store: the directories to put first on <c>PATH</c>, the ones to put
/// last (<see cref="Fallback"/>), and the variables a toolchain needs (<c>DOTNET_ROOT</c>, <c>JAVA_HOME</c>).</summary>
public sealed record RunToolEnvironment(
    IReadOnlyList<string> Path, IReadOnlyDictionary<string, string> Variables, IReadOnlyList<string> Installed, IReadOnlyList<string>? Fallback = null,
    IReadOnlyList<RunToolMount>? Mounts = null)
{
    public static readonly RunToolEnvironment None = new([], new Dictionary<string, string>(), []);

    /// <summary>The zones of the store the run mounts, read-only: only those it has something in.</summary>
    public IReadOnlyList<RunToolMount> Zones => Mounts ?? [];

    /// <summary>
    /// What one run gets from the store's zones: the repository's tools after the core toolchains, both before the agents,
    /// and the agents' Node last. A variable the first zone names is kept.
    /// </summary>
    public static RunToolEnvironment Merge(RunToolEnvironment? agents, RunToolEnvironment? toolchains, RunToolEnvironment? repository)
    {
        RunToolEnvironment[] parts = [.. new[] { toolchains, repository, agents }.OfType<RunToolEnvironment>()];
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts)
            foreach (var (name, value) in part.Variables)
                variables.TryAdd(name, value);
        var first = parts.SelectMany(p => p.Path).Distinct(StringComparer.Ordinal).ToList();
        return new RunToolEnvironment(
            first,
            variables,
            [.. parts.SelectMany(p => p.Installed)],
            [.. parts.SelectMany(p => p.Fallback ?? []).Except(first, StringComparer.Ordinal).Distinct(StringComparer.Ordinal)],
            [.. parts.SelectMany(p => p.Zones)]);
    }

    /// <summary>The variables a toolchain installer needs to find the tools already installed: their <c>PATH</c> ahead of the image's, and what they set.</summary>
    public IReadOnlyList<string> ForInstaller()
        =>
        [
            $"PATH={string.Join(':', Path.Append(ImagePath).Distinct(StringComparer.Ordinal))}",
            .. Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"),
        ];

    /// <summary>The image's own <c>PATH</c>, after everything the store and the run's home add.</summary>
    public const string ImagePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    /// <summary>
    /// The environment every command in the run starts with: the toolchains first on <c>PATH</c>, then the agents, then
    /// <c>$HOME/.local/bin</c>, where <c>npm install -g</c> puts what the agent installs (the store is read-only), then
    /// the <see cref="Fallback"/>: the agents' own Node, so <c>npx</c> starts the MCP servers a provider names in every
    /// run, while a project's own Node comes first. Then the image's own.
    /// </summary>
    public IReadOnlyList<string> For(string home)
    {
        var local = $"{home}/.local";
        var path = string.Join(':', Path.Append($"{local}/bin").Concat(Fallback ?? []).Append(ImagePath).Distinct(StringComparer.Ordinal));
        return
        [
            $"PATH={path}",
            $"NPM_CONFIG_PREFIX={local}",
            .. Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"),
        ];
    }
}

/// <summary>The three zones of the store (docs/adr/0035-run-toolchains.md): each is written by an installer that mounts only it.</summary>
public enum RunToolZone
{
    /// <summary>The agents' Node and the agent CLIs: ILD's own picks.</summary>
    Agents,

    /// <summary>The mise core tools, shared by every repository.</summary>
    Toolchains,

    /// <summary>Everything else a repository names: one folder of the repository's own for each set of specs.</summary>
    Repository,
}

/// <summary>A zone as a run or installer mounts it: <see cref="RepositoryId"/> says which repository's folder for <see cref="RunToolZone.Repository"/>.</summary>
public sealed record RunToolMount(RunToolZone Zone, Guid RepositoryId = default)
{
    /// <summary>Where the zone is mounted. A repository's zone is always at the same path, since a container mounts only one.</summary>
    public string MountPath => Zone switch
    {
        RunToolZone.Agents => RunToolStore.Root,
        RunToolZone.Toolchains => RunToolStore.ToolchainsRoot,
        _ => RunToolStore.RepositoryToolsRoot,
    };

    /// <summary>The name of the volume holding the zone on a container engine, from the store's base name. The agents' is never the base name itself: that volume held everything before the zones, and what a repository's install script could write there is not reused.</summary>
    public string VolumeName(string agentsVolume) => Zone switch
    {
        RunToolZone.Agents => $"{agentsVolume}-agents",
        RunToolZone.Toolchains => $"{agentsVolume}-toolchains",
        _ => $"{agentsVolume}-repo-{RepositoryId:N}",
    };

    /// <summary>The folder of the store's claim the zone is on Kubernetes, mounted as a <c>subPath</c>.</summary>
    public string SubPath => Zone switch
    {
        RunToolZone.Agents => "ild",
        RunToolZone.Toolchains => "toolchains",
        _ => $"repo-tools/{RepositoryId:N}",
    };
}

/// <summary>
/// The shared, read-only store of toolchains and agent CLIs runs mount (docs/adr/0035-run-toolchains.md). ILD fills it
/// by running <see cref="InstallScript"/> as the runner's user in a short-lived installer that mounts it writable:
/// mise installs each toolchain once per version, npm each agent once per version, and each leaves a manifest a later
/// run reads instead of installing again. Runs never run mise; they get the <c>PATH</c> and variables the manifests name.
/// </summary>
public static class RunToolStore
{
    /// <summary>Where the agents' zone is mounted, in its installer and in every run.</summary>
    public const string Root = "/opt/ild-tools";

    /// <summary>Where the core toolchains' zone is mounted.</summary>
    public const string ToolchainsRoot = "/opt/ild-toolchains";

    /// <summary>Where the zone of the run's repository is mounted; its folders are <see cref="RepositoryFolder"/>.</summary>
    public const string RepositoryToolsRoot = "/opt/ild-repo-tools";

    /// <summary>
    /// The folder in a repository's zone for these specs: a short hash of the sorted specs, so when a repository changes
    /// what it names it gets a fresh folder, and what an earlier set installed is not what the new one runs.
    /// </summary>
    public static string RepositoryFolder(IReadOnlyList<RunTool> tools, bool ignoreScripts)
    {
        var specs = string.Join('\n', tools.Select(t => t.ToString()).Order(StringComparer.Ordinal)) + (ignoreScripts ? "\nignore-scripts" : "");
        return $"{RepositoryToolsRoot}/{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(specs)))[..16]}";
    }

    /// <summary>The Node the agent CLIs run on, whatever the project's is.</summary>
    public static readonly RunTool AgentNode = new("node", RunToolDetector.DefaultNodeVersion);

    /// <summary>
    /// The installer's environment: mise's data in <paramref name="root"/>, its own state in the installer's scratch.
    /// Install scripts (npm's postinstall) are off unless <paramref name="ignoreScripts"/> is false, which only the
    /// agents' zone's own npm call and a repository that did not opt out in its own zone allow.
    /// </summary>
    public static IReadOnlyList<string> InstallerEnvironment(string root, bool ignoreScripts)
        =>
        [
            "HOME=/tmp",
            $"ILD_TOOLS_ROOT={root}",
            $"MISE_DATA_DIR={root}/mise",
            $"MISE_CACHE_DIR={root}/cache/mise",
            "MISE_STATE_DIR=/tmp/mise-state",
            "MISE_CONFIG_DIR=/tmp/mise-config",
            "MISE_YES=1",
            "MISE_DOTNET_ISOLATED=1",
            $"npm_config_cache={root}/cache/npm",
            .. ignoreScripts ? new[] { "npm_config_ignore_scripts=true" } : [],
        ];

    /// <summary>The script's arguments: the agents' Node first, then each toolchain, then each agent.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<RunTool> tools, IReadOnlyList<RunAgent> agents)
    {
        var arguments = new List<string>();
        if (agents.Count > 0) arguments.Add($"agentnode={AgentNode}");
        arguments.AddRange(tools.Select(t => $"tool={t}"));
        arguments.AddRange(agents.Select(a => $"agent={a.Agent.Key}|{a.Agent.NpmPackage}|{a.Version}|{a.Agent.BinaryName}"));
        return arguments;
    }

    /// <summary>
    /// Installs what its arguments name, each only when the store lacks it, and prints one JSON line per toolchain and
    /// agent: <c>{"kind":"tool","spec","version","path":[…],"env":{…}}</c>, <c>{"kind":"agent","key","version","path":[…]}</c>.
    /// Installs log to stderr. A lock file per item lets runs, and ILDs, that share the store install at the same time.
    /// </summary>
    public const string InstallScript = """
        set -euo pipefail
        # A failure inside $(…) stops the script too.
        shopt -s inherit_errexit
        # The store's root; another only in tests.
        root="${ILD_TOOLS_ROOT:-/opt/ild-tools}"
        mkdir -p "$root/manifests" "$root/agents" "$root/cache"
        base_path="$PATH"

        manifest_for() { printf '%s/manifests/%s.json' "$root" "$(printf '%s' "$1" | tr -c 'A-Za-z0-9._@-' '_')"; }

        # Runs a command with the PATH and variables a manifest names, as a run would.
        in_tool_env() {
          local m="$1" kv; shift
          (
            while IFS= read -r kv; do export "$kv"; done < <(jq -r '.env | to_entries[] | "\(.key)=\(.value)"' "$m")
            PATH="$(jq -r '.path | join(":")' "$m"):$base_path" "$@"
          )
        }

        # The Azure Artifacts credential provider, pinned. It turns the VSS_NUGET_EXTERNAL_FEED_ENDPOINTS ILD hands a
        # run into credentials for its private NuGet feeds; without it a restore from one fails with a 401.
        credprovider_version=2.0.5

        # Gives a .NET toolchain the credential provider, on its PATH, where NuGet looks for nuget-plugin-* commands
        # from SDK 9.0.200 on. An older SDK does not look there, so it gets none. Installed once per SDK version.
        add_nuget_provider() {
          local m="$1" sdk dir
          sdk="$(in_tool_env "$m" dotnet --version)"
          if ! printf '%s\n' "$sdk" | awk -F. '{ exit !($1 > 9 || ($1 == 9 && ($2 > 0 || $3 + 0 >= 200))) }'; then
            echo "dotnet SDK $sdk: no Azure Artifacts credential provider, which NuGet finds on PATH only from SDK 9.0.200 on" >&2
            jq -c '.nuget = true' "$m" > "$m.nuget"
            mv -f "$m.nuget" "$m"
            return 0
          fi
          dir="$root/nuget-credprovider/$credprovider_version/$sdk"
          mkdir -p "$root/nuget-credprovider/$credprovider_version"
          exec 8>"$dir.lock"; flock 8
          if [ ! -e "$dir/.ild-complete" ]; then
            rm -rf "$dir"
            in_tool_env "$m" env DOTNET_CLI_HOME=/tmp/ild-credprovider NUGET_PACKAGES=/tmp/ild-credprovider/packages \
              DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
              dotnet tool install Microsoft.Artifacts.CredentialProvider.NuGet.Tool \
              --version "$credprovider_version" --tool-path "$dir" >&2
            [ -x "$dir/nuget-plugin-microsoft-artifacts-credential-provider" ]
            touch "$dir/.ild-complete"
          fi
          exec 8>&-
          jq -c --arg dir "$dir" '.path += [$dir] | .nuget = true' "$m" > "$m.nuget"
          mv -f "$m.nuget" "$m"
        }

        # A .NET manifest from before the credential provider (no "nuget" field) gets it once.
        needs_nuget() { [ "${1%%@*}" = dotnet ] && [ "$(jq -r '.nuget // false' "$2")" != true ]; }

        # Installs a toolchain once and leaves its manifest at manifest_for. Called as a command, never inside $(…):
        # a failure there would not stop the script, and the run would start without the toolchain it asked for.
        ensure_tool() {
          local spec="$1" m where envjson
          m="$(manifest_for "$spec")"
          if [ ! -s "$m" ] || needs_nuget "$spec" "$m"; then
            exec 9>"$m.lock"; flock 9
            if [ -s "$m" ] && needs_nuget "$spec" "$m"; then
              cp "$m" "$m.tmp"
              add_nuget_provider "$m.tmp"
              mv -f "$m.tmp" "$m"
            elif [ ! -s "$m" ]; then
              mise install "$spec" >&2
              where="$(mise where "$spec")"
              envjson="$(cd / && PATH="$base_path" mise env --json "$spec")"
              jq -cn --arg spec "$spec" --arg where "$where" --arg base "$base_path" --argjson env "$envjson" '
                ((($env.PATH // "") | split(":")) - ($base | split(":")) | map(select(length > 0))) as $added
                | {spec: $spec, version: ($where | split("/") | last),
                   path: (if ($added | length) > 0 then $added else [$where + "/bin"] end),
                   env: ($env | del(.PATH) | with_entries(select(.key | test("^_?_?MISE") | not)))}' > "$m.tmp"
              [ -s "$m.tmp" ]
              if [ "${spec%%@*}" = dotnet ]; then add_nuget_provider "$m.tmp"; fi
              mv -f "$m.tmp" "$m"
            fi
            exec 9>&-
          fi
        }

        # Installs an agent CLI once, at an exact version, with a wrapper that starts it on the agents' Node.
        ensure_agent() {
          local key="$1" package="$2" version="$3" bin="$4" dir target
          dir="$root/agents/$key/$version"
          if [ ! -e "$dir/.ild-complete" ]; then
            mkdir -p "$root/agents/$key"
            exec 9>"$root/agents/$key/$version.lock"; flock 9
            if [ ! -e "$dir/.ild-complete" ]; then
              rm -rf "$dir"; mkdir -p "$dir/wrap"
              # npm runs only the install scripts its package.json approves: the agent's own, nothing it depends on.
              printf '{"allowScripts":{"%s":true}}' "$package" > "$dir/package.json"
              (cd "$dir" && PATH="$agent_node:$base_path" npm install "$package@$version" --prefix "$dir" \
                --ignore-scripts=false --no-audit --no-fund --no-package-lock) >&2
              target="$(readlink -f "$dir/node_modules/.bin/$bin")"
              if head -n1 "$target" | grep -q '^#!.*node'; then
                printf '#!/bin/sh\nexec "%s/node" "%s" "$@"\n' "$agent_node" "$target" > "$dir/wrap/$bin"
              else
                printf '#!/bin/sh\nexec "%s" "$@"\n' "$target" > "$dir/wrap/$bin"
              fi
              chmod 0755 "$dir/wrap/$bin"
              touch "$dir/.ild-complete"
            fi
            exec 9>&-
          fi
          jq -cn --arg key "$key" --arg version "$version" --arg wrap "$dir/wrap" '{kind: "agent", key: $key, version: $version, path: [$wrap]}'
        }

        agent_node=""
        for arg in "$@"; do
          case "$arg" in
            agentnode=*) spec="${arg#agentnode=}"; ensure_tool "$spec"; agent_node="$(jq -r '.path[0]' "$(manifest_for "$spec")")"; jq -c '. + {kind: "agentnode"}' "$(manifest_for "$spec")" ;;
            tool=*) spec="${arg#tool=}"; ensure_tool "$spec"; jq -c '. + {kind: "tool"}' "$(manifest_for "$spec")" ;;
            agent=*) IFS='|' read -r key package version bin <<< "${arg#agent=}"; ensure_agent "$key" "$package" "$version" "$bin" ;;
          esac
        done
        """;

    /// <summary>The environment the script's output names, toolchains in the order asked, then agents.</summary>
    public static RunToolEnvironment Parse(string output, string? root = null)
    {
        var prefixes = root is null ? new[] { Root + "/", ToolchainsRoot + "/", RepositoryToolsRoot + "/" } : [root.TrimEnd('/') + "/"];
        var tools = new List<(string Spec, List<string> Path, Dictionary<string, string> Env, string Version)>();
        var agents = new List<(string Key, List<string> Path, string Version)>();
        var fallback = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? item;
            try { item = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            var path = item?["path"] is JsonArray entries
                ? entries.Select(e => e?.GetValue<string>()).OfType<string>().Where(p => prefixes.Any(prefix => p.StartsWith(prefix, StringComparison.Ordinal))).ToList()
                : [];
            var version = item?["version"]?.GetValue<string>() ?? "?";
            switch (item?["kind"]?.GetValue<string>())
            {
                case "tool":
                    var env = item["env"] is JsonObject vars
                        ? vars.Where(v => v.Value is JsonValue).ToDictionary(v => v.Key, v => v.Value!.ToString(), StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    tools.Add((item["spec"]?.GetValue<string>() ?? "?", path, env, version));
                    break;
                case "agent":
                    agents.Add((item["key"]?.GetValue<string>() ?? "?", path, version));
                    break;
                case "agentnode":
                    fallback.AddRange(path);
                    break;
            }
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
            foreach (var (name, value) in tool.Env)
                variables.TryAdd(name, value);
        var first = tools.SelectMany(t => t.Path).Concat(agents.SelectMany(a => a.Path)).Distinct(StringComparer.Ordinal).ToList();
        return new RunToolEnvironment(
            first,
            variables,
            tools.Select(t => $"{t.Spec.Split('@')[0]}@{t.Version}").Concat(agents.Select(a => $"{a.Key}@{a.Version}")).ToList(),
            fallback.Except(first, StringComparer.Ordinal).ToList());
    }
}
