using System.Text.Json;
using System.Text.Json.Nodes;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>What a run's commands get from the store: the directories to put first on <c>PATH</c>, the ones to put
/// last (<see cref="Fallback"/>), and the variables a toolchain needs (<c>DOTNET_ROOT</c>, <c>JAVA_HOME</c>).</summary>
public sealed record RunToolEnvironment(
    IReadOnlyList<string> Path, IReadOnlyDictionary<string, string> Variables, IReadOnlyList<string> Installed, IReadOnlyList<string>? Fallback = null)
{
    public static readonly RunToolEnvironment None = new([], new Dictionary<string, string>(), []);

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

/// <summary>
/// The shared, read-only store of toolchains and agent CLIs runs mount (docs/adr/0035-run-toolchains.md). ILD fills it
/// by running <see cref="InstallScript"/> as the runner's user in a short-lived installer that mounts it writable:
/// mise installs each toolchain once per version, npm each agent once per version, and each leaves a manifest a later
/// run reads instead of installing again. Runs never run mise; they get the <c>PATH</c> and variables the manifests name.
/// </summary>
public static class RunToolStore
{
    /// <summary>Where the store is mounted, in the installer and in every run.</summary>
    public const string Root = "/opt/ild-tools";

    /// <summary>The Node the agent CLIs run on, whatever the project's is.</summary>
    public static readonly RunTool AgentNode = new("node", RunToolDetector.DefaultNodeVersion);

    /// <summary>The installer's environment: mise's data in the store, its own state in the installer's scratch.</summary>
    public static IReadOnlyList<string> InstallerEnvironment =>
    [
        "HOME=/tmp",
        $"MISE_DATA_DIR={Root}/mise",
        $"MISE_CACHE_DIR={Root}/cache/mise",
        "MISE_STATE_DIR=/tmp/mise-state",
        "MISE_CONFIG_DIR=/tmp/mise-config",
        "MISE_YES=1",
        "MISE_DOTNET_ISOLATED=1",
        $"npm_config_cache={Root}/cache/npm",
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

        # Installs a toolchain once and leaves its manifest at manifest_for. Called as a command, never inside $(…):
        # a failure there would not stop the script, and the run would start without the toolchain it asked for.
        ensure_tool() {
          local spec="$1" m where envjson
          m="$(manifest_for "$spec")"
          if [ ! -s "$m" ]; then
            exec 9>"$m.lock"; flock 9
            if [ ! -s "$m" ]; then
              mise install "$spec" >&2
              where="$(mise where "$spec")"
              envjson="$(cd / && PATH="$base_path" mise env --json "$spec")"
              jq -cn --arg spec "$spec" --arg where "$where" --arg base "$base_path" --argjson env "$envjson" '
                ((($env.PATH // "") | split(":")) - ($base | split(":")) | map(select(length > 0))) as $added
                | {spec: $spec, version: ($where | split("/") | last),
                   path: (if ($added | length) > 0 then $added else [$where + "/bin"] end),
                   env: ($env | del(.PATH) | with_entries(select(.key | test("^_?_?MISE") | not)))}' > "$m.tmp"
              [ -s "$m.tmp" ]
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
                --no-audit --no-fund --no-package-lock) >&2
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
    public static RunToolEnvironment Parse(string output)
    {
        var tools = new List<(string Spec, List<string> Path, Dictionary<string, string> Env, string Version)>();
        var agents = new List<(string Key, List<string> Path, string Version)>();
        var fallback = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? item;
            try { item = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            var path = item?["path"] is JsonArray entries
                ? entries.Select(e => e?.GetValue<string>()).OfType<string>().Where(p => p.StartsWith(Root + "/", StringComparison.Ordinal)).ToList()
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
