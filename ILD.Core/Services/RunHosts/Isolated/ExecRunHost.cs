using ILD.Core.Services.Implementations.Files;
using ILD.Core.Services.RunHosts.Local;
using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Tools;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>Whose environment a command runs in: a run's own pod or container, or a chat's folder in the shared chat container.</summary>
public readonly record struct ExecOwner(Guid Id, bool Chat)
{
    public static implicit operator ExecOwner(Guid run) => new(run, false);

    public string Home => Chat ? RunPaths.ChatHome(Id) : RunPaths.HomeDirectory(Id);
}

/// <summary>A run (or, with <paramref name="Chat"/>, the container every chat shares) whose pod or container has a direct route to the network now: network mode Off.</summary>
public sealed record DirectRoute(Guid OwnerId, bool Chat);

/// <summary>
/// What the run hosts that put each loop run in an environment of its own (a Kubernetes pod, a container) share: the
/// agent's processes are commands exec'd there, its files are read and written the same way, and code goes in and out
/// as bundles, so the environment never holds the git credential or the mirror. A subclass supplies the exec
/// transport and the lifecycle of the environment: how it is made, found, deleted and swept.
/// </summary>
public abstract class ExecRunHost(IProcessRunner runner, ILogger? logger) : IRunHost
{
    /// <summary>How much of what a run holds or prints ILD reads before it stops.</summary>
    public RunReadLimits ReadLimits { get; init; } = RunReadLimits.Default;

    // Each command is a process group of its own, recorded under a name, so it can be killed as a group
    // without a connection to the command: the runtime does not reliably kill what an exec ran when its stream closes.
    // Arguments: the directory, the command's name, the file its environment is sourced from and removed ("" for none), the entries put in
    // front of its PATH ("" for none), then the command. The environment travels in the file or in the exec's own field, never in the command.
    private const string CommandWrapper = $$"""
        dir=$1; id=$2; envfile=$3; pathprefix=$4; shift 4
        cd -- "$dir" || { [ -z "$envfile" ] || rm -f -- "$envfile"; exit 126; }
        {{EnvironmentPrologue}}
        {{EgressRelayScript}}
        mkdir -p -- "{{RunPaths.ProcessDirectory}}/${id%/*}"
        exec setsid -w sh -c 'f={{RunPaths.ProcessDirectory}}/$1; shift; echo $$ > "$f"; "$@"; rc=$?; rm -f -- "$f"; exit $rc' sh "$id" "$@"
        """;

    private const string EnvironmentPrologue = """
        if [ -n "$envfile" ]; then . "$envfile"; rm -f -- "$envfile"; fi
        if [ -n "$pathprefix" ]; then PATH="$pathprefix:$PATH"; export PATH; fi
        """;

    /// <summary>
    /// Puts a relay on the environment's own loopback between a launch and ILD's egress proxy, and points the launch's proxy variables at it.
    /// The relay adds the launch's proxy credential to each request, so a client that sends none (a browser, a JVM) is still the launch's
    /// to the proxy. One relay per credential, kept for the next launch that holds the same one. Without socat or bash the variables stay
    /// as they are, naming the proxy with the credential.
    /// </summary>
    internal const string EgressRelayScript = $$"""
        case "${HTTP_PROXY:-}" in "http://{{ILD.Core.Services.Implementations.Network.EgressCredentials.User}}:"*@*)
            relay_dir="${ILD_EGRESS_RELAY_DIR:-{{RunPaths.StateDirectory}}/egress}"
            relay_to="${HTTP_PROXY#http://}"; relay_to="${relay_to%/}"
            if command -v socat >/dev/null 2>&1 && command -v bash >/dev/null 2>&1 && mkdir -p -- "$relay_dir" 2>/dev/null; then
                relay_key=$(printf %s "$relay_to" | cksum | cut -d" " -f1)
                exec 9>"$relay_dir/lock"; flock 9 2>/dev/null
                relay_pid=$(cat "$relay_dir/$relay_key.pid" 2>/dev/null); relay_port=$(cat "$relay_dir/$relay_key.port" 2>/dev/null)
                if [ -z "$relay_pid" ] || [ -z "$relay_port" ] || ! grep -qs socat "/proc/$relay_pid/cmdline"; then
                    [ -f "$relay_dir/relay.sh" ] || printf "%s\n" 'IFS= read -r -t 30 line || exit 0
        exec 3<>"/dev/tcp/${ILD_RELAY_UPSTREAM%:*}/${ILD_RELAY_UPSTREAM##*:}" || exit 0
        printf "%s\nProxy-Authorization: Basic %s\r\n" "$line" "$ILD_RELAY_AUTH" >&3
        exec socat STDIO FD:3' > "$relay_dir/relay.sh"
                    relay_port=$(( 20000 + relay_key % 10000 )); relay_try=0; relay_up=
                    while [ -z "$relay_up" ] && [ "$relay_try" -lt 20 ]; do
                        if ! bash -c "exec 3<>/dev/tcp/127.0.0.1/$relay_port" 2>/dev/null; then
                            ILD_RELAY_AUTH=$(printf %s "${relay_to%%@*}" | base64 -w0) ILD_RELAY_UPSTREAM="${relay_to#*@}" setsid socat "TCP-LISTEN:$relay_port,bind=127.0.0.1,reuseaddr,fork" "EXEC:bash $relay_dir/relay.sh,nofork" </dev/null >/dev/null 2>&1 9>&- &
                            relay_pid=$!; relay_wait=0
                            while kill -0 "$relay_pid" 2>/dev/null && [ "$relay_wait" -lt 40 ]; do
                                if bash -c "exec 3<>/dev/tcp/127.0.0.1/$relay_port" 2>/dev/null; then relay_up=1; break; fi
                                sleep 0.05; relay_wait=$((relay_wait+1))
                            done
                            [ -n "$relay_up" ] || kill "$relay_pid" 2>/dev/null
                        fi
                        [ -n "$relay_up" ] || { relay_port=$((relay_port+1)); relay_try=$((relay_try+1)); }
                    done
                    if [ -n "$relay_up" ]; then echo "$relay_pid" > "$relay_dir/$relay_key.pid"; echo "$relay_port" > "$relay_dir/$relay_key.port"; else relay_port=; fi
                fi
                exec 9>&-
                if [ -n "$relay_port" ]; then
                    for relay_var in HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy; do export "$relay_var=http://127.0.0.1:$relay_port"; done
                fi
            fi ;;
        esac
        """;

    /// <summary>
    /// Writes stdin to <c>$1</c>, which only its owner can read: the file holds the environment of a command whose wrapper sources and removes it.
    /// The mode is set before anything is written, whatever the umask of the exec is.
    /// </summary>
    internal const string WriteEnvironmentFileScript = """
        mkdir -p -- "$(dirname -- "$1")" || exit
        umask 077
        rm -f -- "$1" && cat > "$1" && chmod 0600 -- "$1"
        """;

    private const string TerminalWrapper = $$"""
        cd -- "$1" || { [ -z "$2" ] || rm -f -- "$2"; exit 126; }
        envfile=$2; pathprefix=$3; shift 3
        {{EnvironmentPrologue}}
        {{EgressRelayScript}}
        exec "$@"
        """;

    private const string KillCommandScript = $$"""
        f={{RunPaths.ProcessDirectory}}/$1
        [ -f "$f" ] || exit 0
        pid=$(cat -- "$f"); rm -f -- "$f"
        case "$pid" in ''|*[!0-9]*) exit 0 ;; esac
        kill -s TERM -- "-$pid" 2>/dev/null
        i=0
        while kill -0 -- "-$pid" 2>/dev/null && [ "$i" -lt 50 ]; do sleep 0.1; i=$((i+1)); done
        kill -s KILL -- "-$pid" 2>/dev/null
        exit 0
        """;

    internal const string KillAllScript = $$"""
        n=0; pids=
        for f in "{{RunPaths.ProcessDirectory}}/$1"/*; do
            [ -f "$f" ] || continue
            pid=$(cat -- "$f" 2>/dev/null); rm -f -- "$f"
            case "$pid" in ''|*[!0-9]*) continue ;; esac
            kill -0 -- "-$pid" 2>/dev/null || continue
            kill -s TERM -- "-$pid" 2>/dev/null; n=$((n+1)); pids="$pids $pid"
        done
        i=0
        while [ "$i" -lt 50 ]; do
            alive=
            for pid in $pids; do kill -0 -- "-$pid" 2>/dev/null && alive=1; done
            [ -z "$alive" ] && break
            sleep 0.1; i=$((i+1))
        done
        for pid in $pids; do kill -s KILL -- "-$pid" 2>/dev/null; done
        echo "$n"
        """;

    /// <summary>
    /// Writes stdin to <c>$1</c>. A normal write (<c>$2</c> = rw) truncates and writes the file in place, so its mode and
    /// hard links stay, and refuses a symbolic link, whose target ILD cannot check from outside. A read-only write
    /// (<c>$2</c> = ro) replaces the file with a 0444 one.
    /// </summary>
    internal const string WriteFileScript = """
        mkdir -p -- "$(dirname -- "$1")" || exit
        if [ "$2" = ro ]; then
            rm -f -- "$1" && cat > "$1" && chmod 0444 -- "$1"
        else
            [ ! -L "$1" ] || { echo "$1 is a symbolic link" >&2; exit 4; }
            cat > "$1"
        fi
        """;

    /// <summary>
    /// Makes the run's or chat's environment answer the declared forwards (docs/adr/0020) on its own loopback, as ILD
    /// does under <c>dotnet run</c>: one socat per forward listens on <c>127.0.0.1:&lt;local port&gt;</c> and tunnels each
    /// connection to the destination through ILD's egress proxy with <c>CONNECT</c>, so the proxy judges and logs it
    /// as it does every other. A forward with an address (see <see cref="HostsScript"/>) also gets a relay on
    /// <c>&lt;address&gt;:&lt;destination port&gt;</c>, so a client that connects to the forward's real name and port reaches it
    /// and a TLS client checks the certificate against that name. Idempotent: a relay that is running for the same
    /// destination is left alone, one for a changed or deleted forward is stopped. Arguments: the proxy's host and
    /// port, then local port, host, port and address (<c>-</c> for none) per forward. Stdin: the proxy credential
    /// (<c>user:token</c>) the relays present, which is never an argument; nothing for none.
    /// </summary>
    internal const string ForwardsScript = """
        dir="${ILD_FORWARDS_DIR:-/tmp/ild-forwards}"; mkdir -p "$dir"
        proxy_host=$1; proxy_port=$2; shift 2
        proxy_auth=; IFS= read -r -t 5 proxy_auth || true
        want=" "
        relay() {
            key=$1; bind=$2; listen=$3; spec=$4
            want="$want$key "
            if [ -f "$dir/$key.pid" ] && kill -0 "$(cat "$dir/$key.pid")" 2>/dev/null \
                && [ "$(cat "$dir/$key.spec" 2>/dev/null)" = "$spec${proxy_auth:+ auth}" ]; then
                return
            fi
            [ -f "$dir/$key.pid" ] && kill "$(cat "$dir/$key.pid")" 2>/dev/null
            setsid socat "TCP-LISTEN:$listen,bind=$bind,reuseaddr,fork" \
                "PROXY:$proxy_host:$spec,proxyport=$proxy_port${proxy_auth:+,proxyauth=$proxy_auth}" </dev/null >/dev/null 2>&1 &
            echo "$!" > "$dir/$key.pid"; echo "$spec${proxy_auth:+ auth}" > "$dir/$key.spec"
        }
        while [ "$#" -ge 4 ]; do
            local_port=$1; host=$2; port=$3; addr=$4; shift 4
            relay "$local_port" 127.0.0.1 "$local_port" "$host:$port"
            [ "$addr" = "-" ] || relay "${addr}_$port" "$addr" "$port" "$host:$port"
        done
        for f in "$dir"/*.pid; do
            [ -e "$f" ] || continue
            key=$(basename "$f" .pid)
            case "$want" in *" $key "*) ;; *) kill "$(cat "$f")" 2>/dev/null; rm -f "$f" "$dir/$key.spec" ;; esac
        done
        exit 0
        """;

    /// <summary>
    /// Maps each forward's host name to its own loopback address in the environment's hosts file, in a block between
    /// the marker lines that it replaces and removes when no forward is left; every other line stays. The file is
    /// written in place, since the engines bind-mount it and it cannot be renamed over. Arguments: address and host
    /// per forward. Exits 3, changing nothing, when the file cannot be written (kubelet's is root's in a pod whose
    /// commands do not run as root); the caller then keeps the loopback form only.
    /// </summary>
    internal const string HostsScript = """
        PATH=/usr/sbin:/usr/bin:/sbin:/bin; export PATH
        f="${ILD_HOSTS_FILE:-/etc/hosts}"
        [ -w "$f" ] || exit 3
        tmp=$(/usr/bin/mktemp) || exit 1
        trap '/usr/bin/rm -f "$tmp"' EXIT
        /usr/bin/awk '/^# ild-forwards begin$/ { skip = 1; next } /^# ild-forwards end$/ { skip = 0; next } !skip' "$f" > "$tmp" || exit 1
        if [ "$#" -ge 2 ]; then
            echo "# ild-forwards begin" >> "$tmp"
            while [ "$#" -ge 2 ]; do printf '%s %s\n' "$1" "$2" >> "$tmp"; shift 2; done
            echo "# ild-forwards end" >> "$tmp"
        fi
        /usr/bin/cmp -s "$tmp" "$f" && exit 0
        /usr/bin/cat "$tmp" > "$f" || exit 3
        """;

    /// <summary>
    /// Runs <see cref="HostsScript"/>. By default as the user commands run as; a host whose engine makes the hosts file
    /// root's but lets root write it without a capability runs it as root.
    /// </summary>
    protected virtual Task<RunHostOutput> RunHostsScriptAsync(ExecOwner owner, IReadOnlyList<string> arguments, CancellationToken ct)
        => RunScriptAsync(owner, HostsScript, arguments, ct);

    // The owners whose hosts file could not be written, so that is logged once and not on every command.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ExecOwner, bool> _hostsUnwritable = new();

    // A forward's host goes into a socat address, where a comma or a colon would be read as an option or a field.
    private static readonly Regex ForwardHost = new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    /// <summary>The declared forwards, read when a command or terminal starts in a run or chat; null runs with none.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<RunForward>>>? Forwards { get; set; }

    /// <summary>Checks, from the first environment made, that the network leaves no way out but the proxy; null runs no probe.</summary>
    public ILD.Core.Services.Implementations.Network.NetworkEnforcementProbe? Probe { get; set; }

    /// <summary>What the egress proxy refused each toolchain installer, to name in a failed install; null where the host has none.</summary>
    public ILD.Core.Services.Implementations.Network.InstallerRefusals? Refusals { get; set; }

    // The environments that were given relays, so one whose forwards all went is cleaned up, and one that never had
    // any costs no extra exec per command.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ExecOwner, bool> _forwarded = new();

    /// <summary>Brings the relays in <paramref name="owner"/>'s environment in line with the declared forwards. A failure is logged, never thrown: a command that needs no forward must still start.</summary>
    private async Task SyncForwardsAsync(ExecOwner owner, CancellationToken ct)
    {
        if (Forwards is null) return;
        try
        {
            var forwards = (await Forwards(ct)).Where(f => ForwardHost.IsMatch(f.Host) && f.LocalPort is > 0 and <= 65535 && f.Port is > 0 and <= 65535).ToList();
            if (forwards.Count == 0 && !_forwarded.ContainsKey(owner)) return;
            var arguments = new List<string> { new Uri(IldUrl).Host, EgressProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var addresses = await MapForwardHostsAsync(owner, forwards, ct);
            foreach (var f in forwards)
                arguments.AddRange([f.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Host, f.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), addresses.GetValueOrDefault(f.Host, "-")]);
            var result = await RunForwardsScriptAsync(owner, arguments, ct);
            if (result.ExitCode != 0)
                Logger?.LogWarning("Could not start the forwards in the {Noun} of {Owner}: {Error}", Noun, owner.Id, result.StdErr.Trim());
            if (forwards.Count > 0) _forwarded[owner] = true; else _forwarded.TryRemove(owner, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex, "Could not start the forwards in the {Noun} of {Owner}", Noun, owner.Id);
        }
    }

    private async Task<RunHostOutput> RunForwardsScriptAsync(ExecOwner owner, List<string> arguments, CancellationToken ct)
    {
        if (EgressProxyPort <= 0) return await RunScriptAsync(owner, ForwardsScript, arguments, ct);
        var scope = new ILD.Core.Services.Implementations.Network.LaunchScope(
            owner.Chat ? ILD.Core.Services.Implementations.Network.LaunchKind.Chat : ILD.Core.Services.Implementations.Network.LaunchKind.Run, owner.Id);
        using var credential = new MemoryStream(Encoding.UTF8.GetBytes($"{ILD.Core.Services.Implementations.Network.EgressCredentials.User}:{Credentials.Mint(scope)}\n"));
        return await ExecAsync(owner, IldScript("/bin/bash", ForwardsScript, arguments.ToArray()), credential, null, ct);
    }

    /// <summary>
    /// Writes the forward hosts' addresses into the environment's hosts file, and returns the address of each host that
    /// has one: none when the file cannot be written, which is logged once per environment. Docker rewrites the file
    /// when a container joins or leaves a network (opening or closing the direct route), so the block is written
    /// again by the next sync, which runs before every command.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> MapForwardHostsAsync(ExecOwner owner, IReadOnlyList<RunForward> forwards, CancellationToken ct)
    {
        var addresses = ForwardAddresses.Allocate(forwards.Select(f => f.Host));
        var arguments = addresses.SelectMany(a => new[] { a.Value, a.Key }).ToList();
        if (arguments.Count == 0 && !_forwarded.ContainsKey(owner)) return addresses;
        var result = await RunHostsScriptAsync(owner, arguments, ct);
        if (result.ExitCode == 0) { _hostsUnwritable.TryRemove(owner, out _); return addresses; }
        if (_hostsUnwritable.TryAdd(owner, true))
            Logger?.LogWarning("The hosts file of the {Noun} of {Owner} cannot be written, so its forwards answer on 127.0.0.1 and their local port only{Error}",
                Noun, owner.Id, result.ExitCode == 3 ? "" : ": " + result.StdErr.Trim());
        return new Dictionary<string, string>();
    }

    private protected readonly ILogger? Logger = logger;

    /// <summary>The directory of ILD's that holds the files a launch is given to read.</summary>
    internal Func<string> AgentReadRoot { get; init; } = () => AgentIsolation.AgentReadRoot;

    /// <summary>The runs whose environments are kept whatever the caller of <see cref="ListLeftoversAsync"/> says is live: those that have not ended, a failed one included, since it can be retried in the environment it has.</summary>
    /// <summary>What signs the egress proxy credential of each launch (<see cref="ILD.Core.Services.Implementations.Network.EgressCredentials"/>); a key of its own, which no proxy verifies, until the host is given ILD's.</summary>
    public ILD.Core.Services.Implementations.Network.EgressCredentials Credentials { get; init; } = ILD.Core.Services.Implementations.Network.EgressCredentials.Random();

    internal Func<CancellationToken, Task<IReadOnlySet<Guid>>> RetainedRuns { get; init; } = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public abstract RunHostKind Kind { get; }

    /// <summary>The <see cref="RunLeftover.Kind"/> of what this host's sweep finds.</summary>
    public abstract string LeftoverKind { get; }

    public abstract TimeSpan SweepInterval { get; }

    /// <summary>What the environment is called in messages: "pod" or "container".</summary>
    protected abstract string Noun { get; }

    /// <summary>ILD's address as the run reaches it: where the model gateway and the MCP endpoint are.</summary>
    protected abstract string IldUrl { get; }

    /// <summary>
    /// ILD's URL as a run is given it. A run's <c>NO_PROXY</c> names ILD's host and most clients match it as a suffix, so a host that
    /// is a suffix of <c>preview.ild</c> (compose's <c>ild</c>) would send every agent preview around the proxy: runs reach ILD
    /// as <c>&lt;host&gt;-api</c> then, which the environment maps to the same address.
    /// </summary>
    internal static string RunFacingUrl(string ildUrl)
    {
        var uri = new Uri(ildUrl);
        return RunFacingHost(uri.Host) == uri.Host ? ildUrl : new UriBuilder(uri) { Host = RunFacingHost(uri.Host) }.Uri.ToString().TrimEnd('/');
    }

    public static string RunFacingHost(string host)
        => ILD.Core.Services.Interfaces.PreviewProxyBase.AgentHost.Equals(host, StringComparison.OrdinalIgnoreCase)
            || ILD.Core.Services.Interfaces.PreviewProxyBase.AgentHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)
            ? host + "-api"
            : host;

    /// <summary>The addresses the cluster's API server answers on (the endpoints behind the <c>kubernetes</c> service); none where there is no cluster.</summary>
    public virtual Task<IReadOnlyList<System.Net.IPAddress>> ControlPlaneAddressesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<System.Net.IPAddress>>([]);

    /// <summary>
    /// The addresses of the networks and pods this host made for runs, which the egress proxy, being on every one of them, must never
    /// dial for a run: another run's dev server or preview is there. Empty for a host that has none.
    /// </summary>
    public virtual Task<IReadOnlyList<ILD.Core.Services.Implementations.Network.IpRange>> IsolatedRangesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ILD.Core.Services.Implementations.Network.IpRange>>([]);

    /// <summary>The port of ILD's egress proxy; 0 when there is none.</summary>
    protected abstract int EgressProxyPort { get; }

    /// <summary>Whether this host keeps chats: whether a path under <see cref="RunPaths.ChatsRoot"/> is one of its own.</summary>
    public virtual bool HostsChats => false;

    /// <summary>Start a command in the run's environment, with the streams it needs.</summary>
    /// <param name="environment">What the exec is started with, when <see cref="ExecTakesEnvironment"/>; otherwise null.</param>
    protected abstract Task<IExecStream> OpenExecStreamAsync(
        ExecOwner owner, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, CancellationToken ct);

    /// <summary>
    /// Whether the exec transport carries an environment in a field of its own. Where it does not, the environment goes in a file the
    /// command's wrapper sources: the command is part of what the transport records, and no secret may be in it.
    /// </summary>
    protected virtual bool ExecTakesEnvironment => false;

    /// <summary>Whether the run has an environment to exec in.</summary>
    protected abstract Task<bool> RunExistsAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>Check how the host is set up when ILD starts and on every sweep, logging what the operator should fix. Never throws.</summary>
    public virtual Task CheckAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>A warning about how the host is set up that settings should show the operator; null when there is none or it cannot be told.</summary>
    public virtual Task<string?> WarningAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    // -- Lifecycle ---------------------------------------------------------------

    public abstract Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default);

    public abstract Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default);

    // The run's home, with the .local/bin npm install -g writes to. When the run's Node has corepack, its shims go there
    // too, so pnpm and yarn are the versions a project's packageManager pins. A Node without corepack is no failure.
    private const string HomeScript = """
        mkdir -p -- "$1/.local/bin" || exit 1
        if command -v corepack >/dev/null 2>&1; then corepack enable --install-directory "$1/.local/bin" >/dev/null 2>&1 || true; fi
        """;

    /// <summary>What a toolchain installer mounts and runs with: one zone writable, the toolchains read-only when it needs them, and its environment.</summary>
    protected sealed record ToolInstaller(RunToolMount Writable, RunToolMount? ReadOnly, IReadOnlyList<string> Environment);

    /// <summary>
    /// Run <paramref name="work"/> in a short-lived installer of the runner image that mounts only what
    /// <paramref name="installer"/> names, with the network the host gives it, and remove it afterwards. The installer's id
    /// is what <see cref="ExecAsync"/> takes.
    /// </summary>
    protected abstract Task<T> WithToolInstallerAsync<T>(ToolInstaller installer, Func<Guid, Task<T>> work, CancellationToken ct);

    /// <summary>Make the zone's storage exist before its installer mounts it (a volume on a container engine).</summary>
    protected virtual Task PrepareToolZoneAsync(RunToolMount zone, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Where the zone is kept, for the log.</summary>
    protected abstract string ToolZoneName(RunToolMount zone);

    private readonly ConcurrentDictionary<string, RunToolEnvironment> _toolZones = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _toolsGate = new(1, 1);

    /// <summary>
    /// What a run or chat gets from the store (docs/adr/0035-run-toolchains.md), each zone installed first when it lacks
    /// what is asked: the agents and their Node in the agents' zone, the plan's core tools in the shared toolchain zone,
    /// and every other tool of the plan in the zone of <paramref name="repositoryId"/> alone. With no repository (a chat),
    /// those other tools are not installed.
    /// </summary>
    protected async Task<RunToolEnvironment> ToolZonesAsync(string label, RunToolPlan plan, IReadOnlyList<RunAgent> agents, Guid? repositoryId, CancellationToken ct)
    {
        RunToolEnvironment? agentZone = null, toolchainZone = null, repositoryZone = null;
        if (agents.Count > 0)
            agentZone = await ToolZoneAsync(label, new RunToolMount(RunToolZone.Agents), RunToolStore.Root, RunToolStore.Arguments([], agents), true, null, ct);

        var core = plan.Toolchains;
        if (core.Count > 0)
            toolchainZone = await ToolZoneAsync(label, new RunToolMount(RunToolZone.Toolchains), RunToolStore.ToolchainsRoot, RunToolStore.Arguments(core, []), true, null, ct);

        var others = plan.RepositoryTools;
        if (others.Count > 0)
        {
            if (repositoryId is not { } repository)
                Logger?.LogInformation("{Owner} names {Tools}, which only a run's repository holds; it does not get them", label, string.Join(", ", others));
            else
                repositoryZone = await ToolZoneAsync(label, new RunToolMount(RunToolZone.Repository, repository), RunToolStore.RepositoryFolder(others, plan.IgnoreScripts),
                    RunToolStore.Arguments(others, []), plan.IgnoreScripts, toolchainZone, ct);
        }

        return RunToolEnvironment.Merge(agentZone, toolchainZone, repositoryZone);
    }

    private async Task<RunToolEnvironment> ToolZoneAsync(
        string label, RunToolMount zone, string root, IReadOnlyList<string> arguments, bool ignoreScripts, RunToolEnvironment? toolchains, CancellationToken ct)
    {
        var key = string.Join('\n', [zone.Zone.ToString(), zone.RepositoryId.ToString("N"), root, .. arguments]);
        if (_toolZones.TryGetValue(key, out var known)) return known;

        await _toolsGate.WaitAsync(ct);
        try
        {
            if (_toolZones.TryGetValue(key, out known)) return known;
            await PrepareToolZoneAsync(zone, ct);
            if (toolchains is not null) await PrepareToolZoneAsync(new RunToolMount(RunToolZone.Toolchains), ct);

            Logger?.LogInformation("{Owner} gets {Arguments} from the {Zone} zone of the toolchain store, {Name}",
                label, string.Join(", ", arguments), zone.Zone, ToolZoneName(zone));
            var scope = InstallerScope(zone);
            var installer = new ToolInstaller(
                zone,
                toolchains is null ? null : new RunToolMount(RunToolZone.Toolchains),
                [.. RunToolStore.InstallerEnvironment(root, ignoreScripts), .. toolchains?.ForInstaller() ?? [], .. InstallerNetwork(scope)]);
            Refusals?.Begin(scope);
            RunToolEnvironment environment;
            try
            {
                environment = await WithToolInstallerAsync(installer, id => InstallToolsAsync(id, arguments, root, scope, ct), ct);
            }
            finally
            {
                Refusals?.End(scope);
            }
            environment = environment with { Mounts = [zone] };
            Logger?.LogInformation("{Owner} has {Installed}", label, string.Join(", ", environment.Installed));
            return _toolZones[key] = environment;
        }
        finally
        {
            _toolsGate.Release();
        }
    }

    /// <summary>Run the store's install script in <paramref name="installer"/> over <paramref name="arguments"/> and return what its output names under <paramref name="root"/>.</summary>
    private async Task<RunToolEnvironment> InstallToolsAsync(Guid installer, IReadOnlyList<string> arguments, string root, string scope, CancellationToken ct)
    {
        // The script goes in on stdin, read whole before it runs: as an argument it would be part of the exec's URL on
        // Kubernetes, which a long script makes too long for the API server (414).
        using var script = new MemoryStream(Encoding.UTF8.GetBytes(RunToolStore.InstallScript));
        var result = await ExecAsync(installer, [.. ScriptFromStdin, .. arguments], script, null, ct);
        if (!result.Success)
        {
            var tail = string.Join('\n', result.StdErr.Trim().Split('\n').TakeLast(15));
            var refused = Refusals?.Refused(scope) ?? [];
            throw new InvalidOperationException($"could not install the run's toolchains ({string.Join(", ", arguments)}) in the toolchain store: {tail}"
                + (refused.Count == 0 ? "" : $"\nthe egress proxy refused {string.Join(", ", refused)}; see Settings → Network"));
        }
        return RunToolStore.Parse(result.StdOut, root);
    }

    /// <summary>Tries, from inside the environment and without the proxy, to connect out; true when it connected.</summary>
    private async Task<bool?> ConnectDirectlyAsync(ExecOwner owner, string host, int port, CancellationToken ct)
    {
        // Absolute paths, under the fixed PATH and empty environment of IldScript: nothing in the run's own PATH can answer for the probe.
        var result = await RunScriptAsync(owner, "/usr/bin/timeout 3 /bin/bash -c 'exec 3<>\"/dev/tcp/$1/$2\"' probe \"$1\" \"$2\" 2>/dev/null; echo $?",
            [host, port.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
        if (!result.Success) return null;
        return result.StdOut.Trim() switch { "0" => true, "1" or "124" => false, _ => null };
    }

    /// <summary>
    /// Probes the chat container, which runs have no part in and which the probe of a run therefore never covers. Taken under the route
    /// gate and skipped while a chat command in mode Off has the direct route open, since that connection would be no escape.
    /// </summary>
    private Task ProbeChatAsync(ExecOwner owner, ILD.Core.Services.Implementations.Network.NetworkEnforcementProbe probe)
        => probe.ProbeAsync(async (host, port, token) =>
        {
            using var gate = await RouteGateAsync(owner, token);
            if (_liveOff.Any(e => e.Value > 0 && e.Key.Chat) || await HasDirectRouteAsync(owner, token)) return null;
            return await ConnectDirectlyAsync(owner, host, port, token);
        }, CancellationToken.None, ILD.Core.Services.Implementations.Network.NetworkEnforcementProbe.ChatScope);

    /// <summary>Make the environment ready to work in: the run's home and the code the base repository has.</summary>
    protected async Task PrepareAsync(Guid owner, RunEnvironmentRequest request, CancellationToken ct)
    {
        if (Probe is { } probe) _ = probe.ProbeAsync((host, port, token) => ConnectDirectlyAsync(owner, host, port, token));
        var home = await ExecAsync(owner, ["/bin/bash", "-c", HomeScript, "bash", RunPaths.HomeDirectory(owner)], null, null, ct);
        if (!home.Success)
            throw new InvalidOperationException($"could not make the run's home in its {Noun}: {home.StdErr.Trim()}");
        if (request.KeepPreviewData)
            await FillPreviewDataAsync(owner, request.RepositoryId, ct);
        // A retry reaches an environment that already holds the run's checkout, with commits the bundle does not have:
        // cloning into it would fail and checking the branch out again would throw those commits away.
        var workingDirectory = RunPaths.WorkingDirectory(owner);
        if ((await RunScriptAsync(owner, "[ -e \"$1/.git\" ]", [workingDirectory], ct)).Success) return;
        await PopulateAsync(owner, request, workingDirectory, ct);
    }

    // The base repository's branches come in as a bundle, the same way a bundle from the mirror does: the run holds
    // only what the bundle carries, and its origin is the remote.
    private async Task PopulateAsync(Guid owner, RunEnvironmentRequest request, string workingDirectory, CancellationToken ct)
    {
        // The bundle and its directory are gone before the checkout runs.
        using (var bundleFile = PrivateTempFile.Create("ild-run-"))
        {
            await using (var sink = bundleFile.OpenWrite())
            {
                var created = await runner.RunAsync(
                    "git", ["-C", request.BasePath, "bundle", "create", "--quiet", "-", "--all"], "/", ct, null,
                    new ProcessRunOptions(StandardOutputTo: sink));
                if (!created.Success)
                    throw new InvalidOperationException($"could not bundle {request.BasePath} for the run's {Noun}: {created.StdErr.Trim()}");
            }

            var origin = await runner.RunAsync("git", ["-C", request.BasePath, "config", "--get", "remote.origin.url"], "/", ct);
            var originUrl = origin.Success ? RepositoryManager.WithoutUserInfo(origin.StdOut.Trim()) : request.BasePath;

            await using var bundle = bundleFile.OpenRead();
            var pushed = await PushBundleAsync(workingDirectory, originUrl, bundle, ct);
            if (!pushed.Success)
                throw new InvalidOperationException($"could not put the code in the run's {Noun}: {pushed.StdErr.Trim()}");
        }

        var checkout = await RunScriptAsync(
            owner,
            """
            cd -- "$1" || exit 1
            if git rev-parse --verify --quiet "refs/remotes/origin/$2^{commit}" >/dev/null; then
                git checkout --quiet -B "$2" "origin/$2"
            else
                git checkout --quiet -b "$2"
            fi
            """,
            [workingDirectory, request.BranchName], ct);
        if (!checkout.Success)
            throw new InvalidOperationException($"could not check out {request.BranchName} in the run's {Noun}: {checkout.StdErr.Trim()}");
    }

    // -- Commands ----------------------------------------------------------------

    public async Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var psi = command.Start;
        // A command in a chat's folder is the chat's, whatever run or chat id its caller names to kill it by (a title is made under an id of its own).
        // With no directory the id alone says whose command it is, and it is a chat's as readily as a run's.
        var held = command.RunIn is { } runIn ? await HeldOwnerAsync(runIn, ct)
            : string.IsNullOrEmpty(psi.WorkingDirectory) && command.OwnerId != Guid.Empty ? await HeldOwnerAsync(command.OwnerId, ct)
            : null;
        var owner = held ?? (RunPaths.ChatOf(psi.WorkingDirectory) is { } chat && HostsChats
            ? new ExecOwner(chat, Chat: true)
            : command.OwnerId != Guid.Empty ? command.OwnerId : RunPaths.OwnerOf(psi.WorkingDirectory)
                ?? throw new InvalidOperationException($"a command for a {Noun} needs the run it belongs to"));

        // The direct route first, so the launch never runs a moment without it; a proxied launch is where a route that no process
        // of mode Off needs any more is taken away.
        var direct = command.NetworkMode == ILD.Data.Enums.NetworkMode.Off;
        if (!direct)
        {
            await CloseDirectRouteIfUnusedAsync(owner, ct);
            if (owner.Chat && Probe is { } probe && !probe.Probed(ILD.Core.Services.Implementations.Network.NetworkEnforcementProbe.ChatScope))
                _ = ProbeChatAsync(owner, probe);
            return await StartRoutedAsync(command, owner, direct: false, ct);
        }

        // Counted before the route is opened, so a proxied launch checking at the same moment sees it; the process owns the count once it runs.
        _liveOff.AddOrUpdate(owner, 1, (_, n) => n + 1);
        try
        {
            using (await RouteGateAsync(owner, ct))
                await OpenDirectRouteAsync(owner, ct);
            return await StartRoutedAsync(command, owner, direct: true, ct);
        }
        catch
        {
            EndOff(owner);
            throw;
        }
    }

    private async Task<IRunProcess> StartRoutedAsync(RunCommand command, ExecOwner owner, bool direct, CancellationToken ct)
    {
        var psi = command.Start;
        await SyncForwardsAsync(owner, ct);
        var (fileName, arguments) = (psi.FileName, psi.ArgumentList.ToList());
        var merged = await EnvironmentForAsync(command, owner, ct);
        if (command.AsAgent && Path.IsPathRooted(fileName)) fileName = Path.GetFileName(fileName);
        (arguments, var environment) = await StageAgentFilesAsync(owner, command, arguments, merged.Variables, ct);

        var directory = string.IsNullOrEmpty(psi.WorkingDirectory) ? RunPaths.WorkingDirectory(owner.Id) : psi.WorkingDirectory;
        // Only a command started for the run itself is one KillAllAsync ends; a preview service, which has no owner, outlives a retry.
        // A process launched in mode Off is told by its name.
        var id = $"{(command.OwnerId != Guid.Empty ? command.OwnerId.ToString("N") : "unowned")}/{(direct ? OffPrefix : "")}{Guid.NewGuid():N}";
        var (execEnvironment, envFile) = await DeliverEnvironmentAsync(owner, id, environment, ct);
        var wrapped = new List<string> { "/bin/sh", "-c", CommandWrapper, "sh", directory, id, envFile, merged.PathPrefix, fileName };
        wrapped.AddRange(arguments);

        var exec = await OpenExecOrRemoveFileAsync(owner, wrapped, execEnvironment, envFile, stdin: psi.RedirectStandardInput, tty: false, ct);
        return new ExecRunProcess(exec, Noun, id, psi.RedirectStandardInput, (commandId, token) => KillCommandAsync(owner, commandId, token),
            direct ? () => EndOff(owner) : null);
    }

    // -- The direct route (network mode Off) ---------------------------------------

    /// <summary>What the name of a process launched in network mode Off starts with, after its owner's directory in the run's process directory; kill-by-id reads it, the route never does.</summary>
    internal const string OffPrefix = "off-";

    // Off processes this ILD has started and not seen end. This is the only thing that keeps a route open: nothing in the container is trusted,
    // since the run writes its own process files. After a restart it is empty, so the next proxied launch closes the route.
    // A process launched in mode Off is named so, which only kill-by-id reads.
    private readonly ConcurrentDictionary<ExecOwner, int> _liveOff = new();

    // Opening and closing a route are one at a time for a container or pod; every chat shares one.
    private readonly ConcurrentDictionary<ExecOwner, SemaphoreSlim> _routeGates = new();

    private async Task<IDisposable> RouteGateAsync(ExecOwner owner, CancellationToken ct)
    {
        var gate = _routeGates.GetOrAdd(owner.Chat ? new ExecOwner(Guid.Empty, true) : owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private void EndOff(ExecOwner owner) => _liveOff.AddOrUpdate(owner, 0, (_, n) => Math.Max(0, n - 1));

    /// <summary>Give the run's pod or container a route to the network that does not go through ILD. Idempotent. The route is the pod's or container's, not a process's.</summary>
    protected abstract Task OpenDirectRouteAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>Take the direct route away. Idempotent, and nothing when there is none.</summary>
    protected abstract Task CloseDirectRouteAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>Whether the run's pod or container has the direct route now, as the engine or cluster says.</summary>
    protected abstract Task<bool> HasDirectRouteAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>The runs and the chat container that have a direct route now.</summary>
    public abstract Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default);

    private async Task CloseDirectRouteIfUnusedAsync(ExecOwner owner, CancellationToken ct)
    {
        using var gate = await RouteGateAsync(owner, ct);
        if (!await HasDirectRouteAsync(owner, ct)) return;
        // A chat container is every chat's, so one chat's Off process keeps it for all.
        if (_liveOff.Any(e => e.Value > 0 && (owner.Chat ? e.Key.Chat : e.Key == owner))) return;
        await CloseDirectRouteAsync(owner, ct);
    }

    /// <summary>
    /// The environment of a command in the run, merged by <see cref="CommandEnvironment"/>. The run's own defaults are nothing (its
    /// pod or container has its environment), or for a chat its toolchains. The host owns the home and the proxy, which in a proxied
    /// mode the run's network reaches only ILD through: a Cmd node or a preview needs it as much as an agent does. One in network mode Off has the
    /// direct route (OpenDirectRouteAsync) and no proxy, as ADR-0019 says: tools that ignore the proxy variables or are not HTTP work as they
    /// would on the machine itself. A caller's npm prefix is dropped: a run's own was set when its container was made, and a chat, which shares one, gets its own in its home.
    /// </summary>
    private async Task<CommandEnvironment> EnvironmentForAsync(RunCommand command, ExecOwner owner, CancellationToken ct)
    {
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (owner.Chat)
            foreach (var line in await ChatEnvironmentAsync(owner.Id, ct))
                defaults[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];

        var hostOwned = HostOwnedEnvironment(owner, command.NetworkMode, command.ProviderId);
        var merged = CommandEnvironment.Merge(defaults, command.Environment, hostOwned);
        return merged with { Variables = ThroughIld(merged.Variables, hostOwned.Keys) };
    }

    private Dictionary<string, string?> HostOwnedEnvironment(ExecOwner owner, ILD.Data.Enums.NetworkMode mode, Guid? providerId)
    {
        var hostOwned = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = owner.Home, ["NPM_CONFIG_PREFIX"] = owner.Chat ? $"{owner.Home}/.local" : null };
        if (mode != ILD.Data.Enums.NetworkMode.Off && AgentIsolation.EgressProxyUrl(Credentials, new ILD.Core.Services.Implementations.Network.LaunchScope(owner.Chat ? ILD.Core.Services.Implementations.Network.LaunchKind.Chat : ILD.Core.Services.Implementations.Network.LaunchKind.Run, owner.Id, providerId)) is { } proxy)
            foreach (var (name, value) in AgentIsolation.EgressProxyEnvironment(proxy))
                hostOwned[name] = value;
        return hostOwned;
    }

    // ILD is on the run's own network: its address is the one the run reaches it at, and the gateway and the MCP endpoint go direct, not through the proxy.
    private Dictionary<string, string> ThroughIld(IReadOnlyDictionary<string, string> variables, IEnumerable<string> hostOwned)
    {
        var ild = new Uri(IldUrl).Host;
        var result = variables.ToDictionary(e => e.Key, e => ThroughIldHost(e.Value, ild), StringComparer.Ordinal);
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
            if (result.TryGetValue(name, out var noProxy) && hostOwned.Contains(name))
                result[name] = $"{noProxy},{ild}";
        return result;
    }

    /// <summary>
    /// Where the environment of a command goes: the exec's own field when the transport has one, otherwise a file in the run that only its owner
    /// can read, which the command's wrapper sources and removes before it runs the command, so the command's stdin and a terminal are untouched.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string>? Exec, string File)> DeliverEnvironmentAsync(
        ExecOwner owner, string name, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        if (variables.Count == 0) return (null, "");
        if (ExecTakesEnvironment) return (variables, "");

        var path = $"{RunPaths.ProcessDirectory}/{name}.env";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(CommandEnvironment.ShellExports(variables)));
        var written = await ExecAsync(owner, IldScript("/bin/sh", WriteEnvironmentFileScript, path), content, null, ct);
        if (!written.Success)
            throw new IOException($"could not write the command's environment in the run's {Noun}: {written.StdErr.Trim()}");
        return (null, path);
    }

    // The file is the command's wrapper's to remove; a command that never started leaves it.
    private async Task<IExecStream> OpenExecOrRemoveFileAsync(
        ExecOwner owner, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, string envFile, bool stdin, bool tty, CancellationToken ct)
    {
        try
        {
            return await OpenExecAsync(owner, command, environment, stdin, tty, ct);
        }
        catch
        {
            if (envFile.Length > 0)
            {
                try { await RunScriptAsync(owner, "rm -f -- \"$1\"", [envFile], CancellationToken.None); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Logger?.LogWarning(ex, "A command's environment file could not be removed from the run's {Noun}", Noun); }
            }
            throw;
        }
    }

    /// <summary>The name an installer is known by to the egress proxy and the Network Log: its zone, which a repository's is followed by its id.</summary>
    private static string InstallerScope(RunToolMount zone) => zone.Zone switch
    {
        RunToolZone.Agents => "agents",
        RunToolZone.Toolchains => "toolchains",
        _ => $"repo-{zone.RepositoryId:N}",
    };

    /// <summary>
    /// What points an installer's downloads at the egress proxy under the installers' scope: its network reaches only ILD, so
    /// there is no other way out. Nothing is installed without a proxy to go through.
    /// </summary>
    private IEnumerable<string> InstallerNetwork(string scope)
    {
        if (new ILD.Core.Services.Implementations.Network.EgressProxyOptions(EgressProxyPort > 0, EgressProxyPort).InstallerUrl(Credentials, scope) is not { } proxy)
            throw new InvalidOperationException(
                $"could not install the run's toolchains: the egress proxy is not enabled ({AgentIsolation.EgressProxyPortEnvVar} is unset), and a toolchain installer's network reaches only through it");
        var ild = new Uri(IldUrl).Host;
        return AgentIsolation.EgressProxyEnvironment(proxy).Select(e => $"{e.Key}={ThroughIldHost(e.Value, ild)}");
    }

    // The proxy ILD points a launch at listens on ILD's loopback; a run reaches it at ILD's address.
    private string ThroughIldHost(string value, string ildHost)
        => EgressProxyPort > 0
            ? value.Replace($"{IPAddress.Loopback}:{EgressProxyPort}", $"{ildHost}:{EgressProxyPort}", StringComparison.Ordinal)
            : value;

    // The files an adapter wrote for a launch to read (MCP configs, package feed settings, an agent extension) sit in a
    // directory of ILD's that a run cannot see; exactly the declared ones go to memory in it and the launch is pointed
    // at the copies. Text is never searched for paths: a prompt can say anything.
    private async Task<(List<string> Arguments, Dictionary<string, string> Environment)> StageAgentFilesAsync(
        ExecOwner owner, RunCommand command, List<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        if (command.Files.Count == 0) return (arguments, environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
        string root;
        try { root = Path.GetFullPath(AgentReadRoot()); }
        catch (InvalidOperationException) { return (arguments, environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)); }

        var ownerSegment = owner.Id.ToString("N");
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declared in command.Files)
        {
            var local = Path.GetFullPath(declared);
            var relative = Path.GetRelativePath(root, local);
            var inside = relative != "." && !Path.IsPathRooted(relative)
                && !relative.Split(Path.DirectorySeparatorChar).Contains("..");
            if (!inside
                || !relative.Contains(ownerSegment, StringComparison.OrdinalIgnoreCase) || !File.Exists(local))
            {
                Logger?.LogWarning("Not staging {Path} into the run: it is not a file this launch wrote in the agent read root", declared);
                continue;
            }
            staged[declared] = RunPaths.AgentReadDirectory + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            if (local != declared) staged[local] = staged[declared];
        }

        string Rewrite(string text)
        {
            foreach (var (local, remote) in staged.OrderByDescending(e => e.Key.Length))
                text = text.Replace(local, remote, StringComparison.Ordinal);
            return text;
        }

        var rewrittenArguments = arguments.Select(Rewrite).ToList();
        var rewrittenEnvironment = environment.ToDictionary(e => e.Key, e => Rewrite(e.Value), StringComparer.Ordinal);
        foreach (var (local, remote) in staged.DistinctBy(e => e.Value))
        {
            await using var content = File.OpenRead(local);
            await WriteFileInRunAsync(owner, remote, content, readOnly: true, ct);
        }
        return (rewrittenArguments, rewrittenEnvironment);
    }

    private async Task<bool> KillCommandAsync(ExecOwner owner, string commandId, CancellationToken ct)
        => (await RunScriptAsync(owner, KillCommandScript, [commandId], ct)).Success;

    /// <summary>The run or chat this host holds under <paramref name="ownerId"/>, or null when it holds neither.</summary>
    public async Task<ExecOwner?> HeldOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ExecOwner owner = ownerId;
        if (await RunExistsAsync(owner, ct)) return owner;
        owner = new ExecOwner(ownerId, Chat: true);
        return HostsChats && await RunExistsAsync(owner, ct) ? owner : null;
    }

    // The id is a run's or a chat's; a run has an environment of its own and a chat does not, so a run is the one with a pod or container.
    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (await HeldOwnerAsync(ownerId, ct) is not { } owner) return 0;
        var result = await RunScriptAsync(owner, KillAllScript, [ownerId.ToString("N")], ct);
        return result.Success && int.TryParse(result.StdOut.Trim(), out var killed) ? killed : 0;
    }

    // -- Terminal ----------------------------------------------------------------

    public async Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, ILD.Data.Enums.NetworkMode mode, CancellationToken ct = default)
    {
        ExecOwner owner = RunPaths.OwnerOf(options.Cwd)
            ?? throw new InvalidOperationException($"a terminal for a {Noun} needs a working directory in it");

        // The route as StartCommandAsync takes it: the terminal is a process of the run, and one opened in mode Off keeps the direct route until it ends.
        var direct = mode == ILD.Data.Enums.NetworkMode.Off;
        if (direct)
            _liveOff.AddOrUpdate(owner, 1, (_, n) => n + 1);
        else
            await CloseDirectRouteIfUnusedAsync(owner, ct);
        try
        {
            if (direct)
            {
                using (await RouteGateAsync(owner, ct))
                    await OpenDirectRouteAsync(owner, ct);
            }

            var caller = CommandEnvironment.Of(options.Environment);
            caller.TryAdd("TERM", "xterm-256color");
            var hostOwned = HostOwnedEnvironment(owner, mode, providerId: null);
            var merged = CommandEnvironment.Merge(null, caller, hostOwned);
            var variables = ThroughIld(merged.Variables, hostOwned.Keys);
            await SyncForwardsAsync(owner, ct);

            // The terminal is the exec's stdin, so its environment never goes through it: the wrapper sources a file, which prints nothing.
            var (execEnvironment, envFile) = await DeliverEnvironmentAsync(owner, $"{owner.Id:N}/terminal-{Guid.NewGuid():N}", variables, ct);
            var command = new List<string> { "/bin/sh", "-c", TerminalWrapper, "sh", options.Cwd, envFile, merged.PathPrefix, options.App };
            command.AddRange(options.CommandLine ?? []);

            var exec = await OpenExecOrRemoveFileAsync(owner, command, execEnvironment, envFile, stdin: true, tty: true, ct);
            var pty = new ExecPty(exec, direct ? () => EndOff(owner) : null);
            pty.Resize(options.Cols, options.Rows);
            return pty;
        }
        catch
        {
            if (direct) EndOff(owner);
            throw;
        }
    }

    public AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, ILD.Data.Enums.NetworkMode mode)
        => throw new NotSupportedException("the provider sign-in terminal runs on the local run host");

    // -- Bundles -----------------------------------------------------------------

    public async Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
        => ToResult(await ExecAsync(OwnerFor(targetPath), IldScript("/bin/sh", Local.LocalRunHost.CloneScript, targetPath, originUrl), bundle, null, ct));

    public async Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
        => ToResult(await ExecAsync(OwnerFor(clonePath), IldScript("/bin/sh", Local.LocalRunHost.FetchBundleScript, clonePath), bundle, null, ct));

    public async Task<RunHostResult> PullBundleAsync(string clonePath, string branch, bool incremental, Stream sink, CancellationToken ct = default)
    {
        string[] bundle = ["bundle", "create", "--quiet", "-", $"refs/heads/{branch}"];
        IReadOnlyList<string> args = ["git", "-C", clonePath, .. bundle, .. incremental ? new[] { "--not", "--remotes=origin" } : []];
        return ToResult(await ExecAsync(OwnerFor(clonePath), args, null, sink, ct));
    }

    private static RunHostResult ToResult(RunHostOutput output) => new(output.ExitCode, output.StdErr);

    // -- Tools -------------------------------------------------------------------

    public Task<bool> IsRunCloneAsync(string path, CancellationToken ct = default)
        => Task.FromResult(RunPaths.OwnerOf(path) is not null);

    public async Task<RunHostOutput> RunToolAsync(
        string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
    {
        // Under the wrapper like a started command, so a cancelled tool (git in the run) is killed rather than left running.
        var owner = OwnerFor(runPath);
        var id = $"{owner.Id:N}/{Guid.NewGuid():N}";
        try
        {
            return await ExecAsync(owner, ["/bin/sh", "-c", CommandWrapper, "sh", "/", id, "", "", fileName, .. arguments], streams?.StandardInputFrom, streams?.StandardOutputTo, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested || ex is ReadLimitExceededException)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await KillCommandAsync(owner, id, limit.Token); }
            catch (Exception killFailure) when (killFailure is not OutOfMemoryException) { Logger?.LogWarning(killFailure, "A tool in the run's {Noun} that was stopped could not be killed", Noun); }
            throw;
        }
    }

    // -- Files -------------------------------------------------------------------

    public async Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default)
    {
        var limit = ReadLimits.MaxFileBytes;
        var result = await ExecAsync(
            OwnerFor(path), IldScript("/bin/bash", FileWithinLimitScript + "cat -- \"$1\"", path, limit.ToString()), null, null, ct, limit);
        if (result.ExitCode == FileTooLarge) throw new ReadLimitExceededException(limit);
        return result.Success ? result.StdOut : null;
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
        => await ReadAllAsync(path, ct);

    public async Task<Stream?> OpenFileForReadAsync(string path, CancellationToken ct = default)
        => await ReadAllAsync(path, ct);

    public async Task<Stream> OpenFileForWriteAsync(string path, CancellationToken ct = default)
    {
        var content = await ReadAllAsync(path, ct) ?? throw new IOException($"{path} is not a file in the run's {Noun}");
        return new WriteBackStream(content.ToArray(), (bytes, token) => WriteFileInRunAsync(OwnerFor(path), path, new MemoryStream(bytes), readOnly: false, token));
    }

    private const int FileTooLarge = 4;

    /// <summary>Exits 3 when <c>$1</c> is not a file and <see cref="FileTooLarge"/> when it is longer than <c>$2</c> bytes, so a file past the limit is never started on.</summary>
    private const string FileWithinLimitScript = "[ -f \"$1\" ] || exit 3; [ \"$(stat -c %s -- \"$1\")\" -le \"$2\" ] || exit 4; ";

    private async Task<MemoryStream?> ReadAllAsync(string path, CancellationToken ct)
    {
        var content = new MemoryStream();
        var limit = ReadLimits.MaxFileBytes;
        var result = await ExecAsync(
            OwnerFor(path), IldScript("/bin/sh", FileWithinLimitScript + "exec cat -- \"$1\"", path, limit.ToString()), null, new CappedWriteStream(content, limit), ct);
        if (result.ExitCode == FileTooLarge) throw new ReadLimitExceededException(limit);
        if (!result.Success) return null;
        content.Position = 0;
        return content;
    }

    public async Task<long?> GetFileLengthAsync(string path, CancellationToken ct = default)
    {
        var result = await RunScriptAsync(OwnerFor(path), "[ -f \"$1\" ] || exit 3; stat -c %s -- \"$1\"", [path], ct);
        return result.Success && long.TryParse(result.StdOut.Trim(), out var length) ? length : null;
    }

    private async Task<Stream?> ReadRangeAsync(string path, long offset, long count, CancellationToken ct = default)
    {
        // The range is held in memory, so one longer than a file may be is refused before the command runs.
        if (count > ReadLimits.MaxFileBytes) throw new ReadLimitExceededException(ReadLimits.MaxFileBytes);
        var content = new MemoryStream();
        var result = await ExecAsync(
            OwnerFor(path), IldScript("/bin/sh", "[ -f \"$1\" ] || exit 3; tail -c \"+$(( $2 + 1 ))\" -- \"$1\" | head -c \"$3\"", path, offset.ToString(), count.ToString()),
            null, new CappedWriteStream(content, ReadLimits.MaxFileBytes), ct);
        if (!result.Success) return null;
        content.Position = 0;
        return content;
    }

    public async Task ReadFileRangeAsync(string path, long offset, long count, Func<Stream, Task> consume)
    {
        await using var range = await ReadRangeAsync(path, offset, count) ?? throw new FileNotFoundException($"{path} is not a file in the run's {Noun}", path);
        await consume(range);
    }

    public async Task WriteFileAsync(string path, Stream content, CancellationToken ct = default)
        => await WriteFileInRunAsync(OwnerFor(path), path, content, readOnly: false, ct);

    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await WriteFileInRunAsync(OwnerFor(path), path, bytes, readOnly: false, ct);
    }

    public async Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default)
    {
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await WriteFileInRunAsync(OwnerFor(path), path, bytes, readOnly: true, ct);
    }

    private async Task WriteFileInRunAsync(ExecOwner owner, string path, Stream content, bool readOnly, CancellationToken ct)
    {
        var result = await ExecAsync(
            owner,
            IldScript("/bin/sh", WriteFileScript, path, readOnly ? "ro" : "rw"),
            content, null, ct);
        if (!result.Success)
            throw new IOException($"could not write {path} in the run's {Noun}: {result.StdErr.Trim()}");
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var result = await RunScriptAsync(OwnerFor(path), "mkdir -p -- \"$1\"", [path], ct);
        if (!result.Success)
            throw new IOException($"could not create {path} in the run's {Noun}: {result.StdErr.Trim()}");
    }

    public async Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
        => (await RunScriptAsync(OwnerFor(path), "[ -f \"$1\" ] && [ ! -L \"$1\" ]", [path], ct)).Success;

    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
        => (await RunScriptAsync(OwnerFor(path), "[ -d \"$1\" ]", [path], ct)).Success;

    public async Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(string directory, string namePattern, CancellationToken ct = default)
    {
        const string Script = """
            [ -d "$1" ] || exit 0
            find -P "$1" -type f -name "$2" -exec sh -c 'for f; do printf "%s\0%s\0" "$f" "$(head -n 1 -- "$f")"; done' sh {} +
            """;
        var result = await RunScriptAsync(OwnerFor(directory), Script, [directory, namePattern], ct);
        var parts = result.StdOut.Split('\0');
        var files = new List<(string, string)>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            files.Add((parts[i], parts[i + 1]));
        return files;
    }

    public async Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var all = true;
        foreach (var group in paths.GroupBy(OwnerFor))
        {
            // A run that is not running is not started again to delete files in it: they go with its pod or container, and
            // what a crashed run left must not run again. A chat's files outlive its container.
            if (!group.Key.Chat && !await IsRunningAsync(group.Key.Id, ct)) continue;
            var result = await RunScriptAsync(group.Key, "rm -rf -- \"$@\"", [.. group], ct);
            all &= result.Success;
            if (result.Success && HostsChats)
                foreach (var path in group)
                    if (RunPaths.ChatOf(path) is { } chat && path.TrimEnd('/') == RunPaths.ChatRoot(chat))
                        ChatRemoved(chat);
        }
        return all;
    }

    // -- Preview and gateway -----------------------------------------------------

    public string? RunHomeFor(string workingDirectory)
        => RunPaths.OwnerOf(workingDirectory) is { } owner ? RunPaths.HomeDirectory(owner)
            : HostsChats && RunPaths.ChatOf(workingDirectory) is { } chat ? RunPaths.ChatHome(chat)
            : null;

    public string GatewayApiUrl(string? configuredApiUrl, string? workingDirectory) => IldUrl;

    public string PreviewStateDirectoryFor(string worktreePath)
        => RunPaths.OwnerOf(worktreePath) is { } run ? $"{RunPaths.RunRoot(run)}/preview-state"
            : HostsChats && RunPaths.ChatOf(worktreePath) is { } chat ? $"{RunPaths.ChatRoot(chat)}/preview-state/{WorktreePreviewService.WorktreeSlug(worktreePath)}"
            : throw new InvalidOperationException($"{worktreePath} is not a worktree in a run's or chat's {Noun}");

    public async Task<string?> PreviewDataFolderAsync(Guid repositoryId, string worktreePath, CancellationToken ct = default)
    {
        if (RunPaths.OwnerOf(worktreePath) is not { } run)
            return HostsChats && RunPaths.ChatOf(worktreePath) is not null
                ? null
                : throw new InvalidOperationException($"{worktreePath} is not a worktree in a run's or chat's {Noun}");
        var folder = RunPaths.PreviewData(run);
        return (await RunScriptAsync(run, "[ -d \"$1\" ]", [folder], ct)).Success ? folder : null;
    }

    /// <summary>Whether the run's pod or container is there and running, found without starting it.</summary>
    protected abstract Task<bool> IsRunningAsync(Guid owner, CancellationToken ct);

    public Task<bool> IsRunningAsync(RunEnvironment environment, CancellationToken ct = default) => IsRunningAsync(environment.OwnerId, ct);

    /// <summary>Where the preview data repositories keep between runs is stored, on ILD's own disk. A run made with <see cref="RunEnvironmentRequest.KeepPreviewData"/> needs it.</summary>
    public PreviewDataStore? PreviewData { get; set; }

    // The exit status of the pack script when the run has no copy to put back.
    private const int NoPreviewDataCopy = 3;

    /// <summary>Fills the run's own copy from the store, unless it has one already (a retry of <see cref="CreateAsync"/>).</summary>
    private async Task FillPreviewDataAsync(Guid owner, Guid repositoryId, CancellationToken ct)
    {
        var store = PreviewData ?? throw new InvalidOperationException($"this run host has no store of preview data to fill the run's {Noun} from");
        var folder = RunPaths.PreviewData(owner);
        if ((await RunScriptAsync(owner, "[ -e \"$1\" ] || [ -L \"$1\" ]", [folder], ct)).Success) return;
        using var file = PrivateTempFile.Create("ild-preview-data-");
        await using (var sink = file.OpenWrite())
            await store.PackAsync(repositoryId, sink, ct);
        await using var archive = file.OpenRead();
        var filled = await ExecAsync(owner, IldScript("/bin/bash", "mkdir -p -- \"$1\" && exec tar -x --no-same-owner -C \"$1\"", folder), archive, null, ct);
        if (!filled.Success)
            throw new InvalidOperationException($"could not fill the run's copy of its repository's preview data in its {Noun}: {filled.StdErr.Trim()}");
    }

    /// <summary>
    /// Puts the run's copy of the preview data back in the store of <see cref="RunEnvironment.KeptPreviewDataOf"/>, before the
    /// run's objects are deleted. Only from a run that is still running: one whose pod or container stopped or went away ended some other
    /// way, and what it left is not kept. Never throws; the run is deleted whatever happened here.
    /// </summary>
    protected async Task SyncPreviewDataBackAsync(RunEnvironment environment, CancellationToken ct)
    {
        if (environment.KeptPreviewDataOf is not { } repositoryId || PreviewData is not { } store) return;
        var owner = environment.OwnerId;
        try
        {
            // Asked first: running a command in a stopped run would start it again.
            if (!await IsRunningAsync(owner, ct))
            {
                Logger?.LogWarning("Run {Run} is not running, so its copy of the preview data is not kept", owner);
                return;
            }
            using var file = PrivateTempFile.Create("ild-preview-data-");
            RunHostOutput packed;
            await using (var sink = file.OpenWrite())
                packed = await ExecAsync(
                    owner, IldScript("/bin/bash", $"[ -d \"$1\" ] || exit {NoPreviewDataCopy}; exec tar -c -C \"$1\" .", RunPaths.PreviewData(owner)),
                    null, new CappedWriteStream(sink, ReadLimits.MaxPreviewDataBytes), ct);
            if (packed.ExitCode == NoPreviewDataCopy) return;
            if (!packed.Success)
            {
                Logger?.LogWarning("Could not pack the preview data of run {Run}; the stored copy is unchanged: {Error}", owner, packed.StdErr.Trim());
                return;
            }
            await using var archive = file.OpenRead();
            await store.ReplaceAsync(repositoryId, archive, ct);
        }
        catch (ReadLimitExceededException)
        {
            Logger?.LogWarning("The preview data of run {Run} is larger than the {Limit} MB ILD reads, so the stored copy is unchanged", owner, ReadLimits.MaxPreviewDataBytes / RunReadLimits.Megabyte);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex, "The preview data of run {Run} is not kept: it could not be put back from its {Noun}", owner, Noun);
        }
    }

    /// <summary>Where the preview proxy forwards to reach a service listening on <paramref name="port"/> in the run.</summary>
    public abstract Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default);

    // -- Sweep -------------------------------------------------------------------

    public abstract Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default);

    public abstract Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default);

    // -- Chats -------------------------------------------------------------------

    /// <summary>Whether chats get toolchains from the store: whether this host mounts it.</summary>
    protected abstract bool ChatToolchains { get; }

    /// <summary>Where what each chat's repositories need from the toolchain store is kept. Replaced in tests.</summary>
    internal Func<string> ChatToolsRoot { get; init; } = () => Path.Combine(ManagedAgentInstall.ResolveDataRoot(), "chat-tools");

    private readonly ConcurrentDictionary<Guid, List<RunTool>> _chatTools = new();

    /// <summary>
    /// What the commands of a run or chat get from the toolchain store for <paramref name="plan"/>, installed into it first
    /// when it lacks them; null when there is no store. <paramref name="repositoryId"/> is the run's repository, null for a chat.
    /// </summary>
    protected abstract Task<RunToolEnvironment?> ToolsForAsync(string label, RunToolPlan plan, Guid? repositoryId, CancellationToken ct);

    // What the chat's repositories need, kept on ILD's side: a chat's agent writes everything in the chat's folder, and
    // what is asked of the toolchain store (which every run and chat shares) must not be something it can write.
    private string ChatToolsFile(Guid chat) => Path.Combine(ChatToolsRoot(), $"{chat:N}.json");

    private List<RunTool> ChatToolsOf(Guid chat)
        => _chatTools.GetOrAdd(chat, _ =>
        {
            try
            {
                var file = ChatToolsFile(chat);
                return File.Exists(file)
                    ? (System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? []).Select(RunTool.Parse).OfType<RunTool>().ToList()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Logger?.LogWarning(ex, "Could not read the toolchains of chat {Chat}; it gets none until a repository is cloned into it again", chat);
                return [];
            }
        });

    /// <summary>Adds what the repository at <paramref name="basePath"/> needs to the chat's toolchains, installing them now.</summary>
    public async Task AddChatToolsAsync(string chatPath, string basePath, CancellationToken ct = default)
    {
        if (!ChatToolchains || RunPaths.ChatOf(chatPath) is not { } chat) return;

        // The installer runs in another container, so nothing in the chat's own would show this work.
        using var hold = HoldChat(chatPath);

        // Every chat shares one container, so a chat gets the shared toolchains only: what only a repository's own runs hold stays out.
        var plan = RunToolDetector.Resolve(basePath);
        foreach (var skipped in plan.RepositoryTools)
            Logger?.LogInformation("Chat {Chat} does not get {Tool} from {BasePath}: only a run's repository holds a tool that is not a mise core tool", chat, skipped, basePath);
        var known = ChatToolsOf(chat);
        var added = false;
        lock (known)
        {
            foreach (var tool in plan.Toolchains)
            {
                if (known.FirstOrDefault(t => t.Name == tool.Name) is { } existing)
                {
                    if (existing != tool)
                        Logger?.LogWarning("Chat {Chat} already has {Existing}, so {Tool} from {BasePath} is not added", chat, existing, tool, basePath);
                    continue;
                }
                known.Add(tool);
                added = true;
            }
            if (added)
            {
                Directory.CreateDirectory(ChatToolsRoot());
                File.WriteAllText(ChatToolsFile(chat), System.Text.Json.JsonSerializer.Serialize(known.Select(t => t.ToString())));
            }
        }

        // Installed now, in the clone call, rather than on the chat's next command.
        if (added) await ChatEnvironmentAsync(chat, ct);
    }

    /// <summary>The variables (<c>NAME=value</c>) every command of the chat starts with besides its caller's: its toolchains on <c>PATH</c>.</summary>
    protected async Task<IReadOnlyList<string>> ChatEnvironmentAsync(Guid chat, CancellationToken ct)
    {
        if (!ChatToolchains) return [];

        List<RunTool> tools;
        var known = ChatToolsOf(chat);
        lock (known) tools = [.. known];
        var tooling = await ToolsForAsync($"Chat {chat}", new RunToolPlan(tools, tools.Select(t => t.ToString()).ToList()), null, ct);
        return tooling?.For(RunPaths.ChatHome(chat)) ?? [];
    }

    /// <summary>The chat's folder was deleted: forget what was kept about it.</summary>
    private void ChatRemoved(Guid chat)
    {
        _chatTools.TryRemove(chat, out _);
        try { File.Delete(ChatToolsFile(chat)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }


    public string ChatFolderPath(Guid chatId, string localRoot)
        => HostsChats ? RunPaths.ChatRoot(chatId) : throw new NotSupportedException($"this host does not keep chats in a {Noun}");

    public async Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default)
    {
        if (!HostsChats) throw new NotSupportedException($"this host does not keep chats in a {Noun}");
        var home = await ExecAsync(new ExecOwner(chatId, Chat: true), ["/bin/bash", "-c", HomeScript, "bash", RunPaths.ChatHome(chatId)], null, null, ct);
        if (!home.Success)
            throw new IOException($"could not make the chat's folder in the {Noun}: {home.StdErr.Trim()}");
        return RunPaths.ChatRoot(chatId);
    }

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct = default)
    {
        const string Script = """for d in "$1"/*/; do d="${d%/}"; [ -d "$d" ] && [ ! -L "$d" ] && printf '%s\0' "${d##*/}"; done; exit 0""";
        var result = await RunScriptAsync(OwnerFor(directory), Script, [directory], ct);
        return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<bool> EntryExistsAsync(string path, CancellationToken ct = default)
        => (await RunScriptAsync(OwnerFor(path), "[ -e \"$1\" ] || [ -L \"$1\" ]", [path], ct)).Success;

    // -- Chat idleness -----------------------------------------------------------

    /// <summary>The clock the chat's idleness is measured on.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long the chat container may be idle before <see cref="ReapIdleChatAsync"/> removes it; zero never does.</summary>
    protected virtual TimeSpan ChatIdleTimeout => TimeSpan.Zero;

    // Open chat work: execs (turns, commands, previews, clones) and holds. The reaper sets _chatRemoving, then reads the count;
    // work adds to the count, then reads the flag (both Interlocked), so one of them always sees the other.
    private int _chatOpen;
    private long _chatLast;
    private int _chatRemoving;
    private int _removedForIdle;

    /// <summary>Whether the reaper is about to remove, or removing, the chat container: <c>EnsureChat*</c> then waits for the gate instead of trusting what it sees.</summary>
    protected bool ChatBeingRemoved => Volatile.Read(ref _chatRemoving) != 0;

    /// <summary>Whether the chat container was removed for idleness and has not come back since; reads once.</summary>
    protected bool TakeRemovedForIdle() => Interlocked.Exchange(ref _removedForIdle, 0) != 0;

    private void BeginChatWork()
    {
        Interlocked.Increment(ref _chatOpen);
        Interlocked.Exchange(ref _chatLast, Time.GetUtcNow().UtcTicks);
    }

    private void EndChatWork()
    {
        // Stamped first, so the reaper never sees no open work together with a stale time.
        Interlocked.Exchange(ref _chatLast, Time.GetUtcNow().UtcTicks);
        Interlocked.Decrement(ref _chatOpen);
    }

    // Idle: nothing open, and the last start or end was at least the timeout ago. The first look only starts the clock, for a
    // container that was there before this ILD did.
    private bool IsChatIdle()
    {
        var timeout = ChatIdleTimeout;
        if (timeout <= TimeSpan.Zero || Volatile.Read(ref _chatOpen) != 0) return false;
        var now = Time.GetUtcNow().UtcTicks;
        if (Interlocked.CompareExchange(ref _chatLast, now, 0) == 0) return false;
        return now - Interlocked.Read(ref _chatLast) >= timeout.Ticks;
    }

    /// <summary>
    /// Removes the chat container or pod when it has been idle for the timeout; its volume or claim and network stay. Never
    /// touches a run's container or pod. Does nothing by default.
    /// </summary>
    public virtual Task ReapIdleChatAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The reaper's check and removal, under the chat's <paramref name="gate"/> so it never interleaves with <c>EnsureChat*</c>.</summary>
    protected async Task ReapChatAsync(SemaphoreSlim gate, string name, Func<CancellationToken, Task> remove, CancellationToken ct)
    {
        if (!IsChatIdle()) return;

        await gate.WaitAsync(ct);
        try
        {
            if (!IsChatIdle() || !await RunExistsAsync(new ExecOwner(Guid.Empty, Chat: true), ct)) return;

            Interlocked.Exchange(ref _chatRemoving, 1);
            try
            {
                // Work that began after the check above, and before the flag, is seen here.
                if (!IsChatIdle()) return;
                await remove(ct);
                Interlocked.Exchange(ref _removedForIdle, 1);
                Logger?.LogInformation("Removed the chat {Noun} {Name}: it was idle for {Minutes} minutes; the next chat makes it again", Noun, name, (int)ChatIdleTimeout.TotalMinutes);
            }
            finally
            {
                Interlocked.Exchange(ref _chatRemoving, 0);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Counts the chat that holds <paramref name="chatPath"/> as busy until the result is disposed.</summary>
    public IDisposable HoldChat(string chatPath)
    {
        if (!HostsChats) return ChatWork.None;
        BeginChatWork();
        return new ChatWork(EndChatWork);
    }

    private sealed class ChatWork(Action? end) : IDisposable
    {
        public static readonly ChatWork None = new(null);
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }

    // Counts one chat exec as open until its command ends or the stream is disposed, whichever comes first.
    private sealed class ChatExecStream : IExecStream
    {
        private readonly IExecStream _inner;
        private readonly ChatWork _work;

        public ChatExecStream(IExecStream inner, ChatWork work)
        {
            _inner = inner;
            _work = work;
            Status = Track(inner.Status);
        }

        public Stream Stdout => _inner.Stdout;
        public Stream? Stderr => _inner.Stderr;
        public Task<int?> Status { get; }

        private async Task<int?> Track(Task<int?> status)
        {
            try { return await status.ConfigureAwait(false); }
            finally { _work.Dispose(); }
        }

        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _inner.WriteStdinAsync(data, ct);
        public Task CloseStdinAsync(CancellationToken ct = default) => _inner.CloseStdinAsync(ct);
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => _inner.ResizeAsync(cols, rows, ct);

        public void Dispose()
        {
            _inner.Dispose();
            _work.Dispose();
        }
    }

    // -- Exec --------------------------------------------------------------------

    protected ExecOwner OwnerFor(string path)
        => RunPaths.OwnerOf(path) is { } run ? run
            : RunPaths.ChatOf(path) is { } chat && HostsChats ? new ExecOwner(chat, Chat: true)
            : throw new InvalidOperationException($"{path} is not a path in a run's or chat's {Noun}");

    private protected async Task<IExecStream> OpenExecAsync(
        ExecOwner owner, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, CancellationToken ct)
    {
        // Counted before the stream is opened, since opening it is what makes the chat container when it is missing.
        ChatWork? work = null;
        if (owner.Chat)
        {
            BeginChatWork();
            work = new ChatWork(EndChatWork);
        }
        try
        {
            var stream = await OpenExecStreamAsync(owner, command, environment, stdin, tty, ct);
            return work is null ? stream : new ChatExecStream(stream, work);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            work?.Dispose();
            throw new InvalidOperationException($"could not run a command in the {Noun} of {(owner.Chat ? "chat" : "run")} {owner.Id:N}: {ex.Message}", ex);
        }
        catch
        {
            work?.Dispose();
            throw;
        }
    }

    // -- Provider sign-in --------------------------------------------------------

    /// <summary>The home of the CLI in a sign-in container or pod, where it writes its login.</summary>
    protected const string SignInHome = "/tmp/home";

    // Reads one regular, non-empty file of at most the import's cap in the home, as base64; nothing else is printed.
    private const string ReadLoginScript = """
        f="$3/$1"
        { [ -f "$f" ] && [ ! -L "$f" ]; } || exit 3
        size=$(stat -c %s -- "$f") || exit 3
        { [ "$size" -gt 0 ] && [ "$size" -le "$2" ]; } || exit 3
        base64 -w0 -- "$f"
        """;

    /// <summary>The text of a login file in a sign-in environment's home, by its path relative to it; null when it is not a regular file of a size worth reading.</summary>
    protected async Task<string?> ReadSignInFileAsync(Guid id, string relativePath, CancellationToken ct)
    {
        var result = await RunScriptAsync(id, ReadLoginScript, [relativePath, CredentialImport.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), SignInHome], ct);
        if (result.ExitCode == 3) return null;
        if (!result.Success)
            throw new InvalidOperationException($"could not read {relativePath} in the sign-in {Noun}: {result.StdErr.Trim()}");
        return Encoding.UTF8.GetString(Convert.FromBase64String(result.StdOut.Trim()));
    }

    /// <summary>The command that starts a sign-in CLI in its home, in a terminal.</summary>
    protected static IReadOnlyList<string> SignInTerminalCommand(string command, IReadOnlyList<string> arguments)
        => ["/bin/sh", "-c", "mkdir -p -- \"$HOME\" && cd -- \"$HOME\" && exec env TERM=xterm-256color \"$@\"", "sh",
            Path.IsPathRooted(command) ? Path.GetFileName(command) : command, .. arguments];

    // Reads a script from stdin into a file, then runs it with the arguments, so what the script runs does not read
    // the rest of the script as its own input.
    private static readonly string[] ScriptFromStdin =
        ["/bin/bash", "-c", "f=$(mktemp) && cat > \"$f\" && exec bash \"$f\" \"$@\"", "bash"];

    /// <summary>The PATH ILD's own scripts run with in a run: the system's directories only, none the agent writes to.</summary>
    internal const string FixedPath = "/usr/sbin:/usr/bin:/sbin:/bin";

    /// <summary>
    /// A script of ILD's own as a command: run with an empty environment and <see cref="FixedPath"/>, so nothing the agent put in the
    /// run's PATH, BASH_ENV or ENV is run by it. The environment is cleared by the command because an exec's Env can only add.
    /// </summary>
    internal static string[] IldScript(string shell, string script, params string[] arguments)
        => ["/usr/bin/env", "-i", "PATH=" + FixedPath, shell, "-c", script, Path.GetFileName(shell), .. arguments];

    protected Task<RunHostOutput> RunScriptAsync(ExecOwner owner, string script, IReadOnlyList<string> arguments, CancellationToken ct)
        => ExecAsync(owner, IldScript("/bin/bash", script, arguments.ToArray()), null, null, ct);

    /// <summary>
    /// A command in the run, to its end. <paramref name="stdin"/> is sent and closed; <paramref name="stdout"/> receives
    /// the output when given, otherwise it is collected as text.
    /// </summary>
    private async Task<RunHostOutput> ExecAsync(
        ExecOwner owner, IReadOnlyList<string> command, Stream? stdin, Stream? stdout, CancellationToken ct, long? collectedLimit = null)
    {
        using var exec = await OpenExecAsync(owner, command, environment: null, stdin is not null, tty: false, ct);
        using var collected = new MemoryStream();
        using var errors = new MemoryStream();
        using var errorsKept = new TruncatingWriteStream(errors, StderrKeptBytes);
        // Output ILD keeps stops at the limit: the command is stopped there, not read to its end and cut afterwards.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = Drain(exec.Stdout, stdout ?? new CappedWriteStream(collected, collectedLimit ?? ReadLimits.MaxOutputBytes), stop);
        var failures = Drain(exec.Stderr!, errorsKept, stop);

        int? status;
        try
        {
            if (stdin is not null)
            {
                try
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await stdin.ReadAsync(buffer, stop.Token)) > 0)
                        await exec.WriteStdinAsync(buffer.AsMemory(0, read), stop.Token);
                    await exec.CloseStdinAsync(stop.Token);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
                {
                    // The command ended without reading it all; its status says how.
                }
            }

            status = await exec.Status.WaitAsync(stop.Token);
            await Task.WhenAll(output, failures);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stop.IsCancellationRequested)
        {
            await output;
            throw;
        }
        var stderr = Encoding.UTF8.GetString(errors.ToArray());
        if (errorsKept.Truncated)
            stderr += "\n…truncated\n";
        if (status is null)
            stderr += $"the connection to the run's {Noun} ended before the command did\n";
        return new RunHostOutput(status ?? ExecRunProcess.ConnectionLostExitCode, Encoding.UTF8.GetString(collected.ToArray()), stderr);
    }

    /// <summary>What of a command's stderr is kept: it is read by a person, so past this it is cut with a marker.</summary>
    private const int StderrKeptBytes = 64 * 1024;

    private static async Task Drain(Stream from, Stream to, CancellationTokenSource stop)
    {
        try { await from.CopyToAsync(to, stop.Token); }
        catch (ReadLimitExceededException)
        {
            stop.Cancel();
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
    }

    private sealed class WriteBackStream : MemoryStream
    {
        private readonly Func<byte[], CancellationToken, Task> _store;
        private readonly byte[] _original;
        private bool _stored;

        public WriteBackStream(byte[] content, Func<byte[], CancellationToken, Task> store)
        {
            _store = store;
            _original = content;
            Write(content);
            Position = 0;
        }

        /// <summary>Only an edit is stored back: a stream nobody wrote to, or saved with the same bytes, leaves the file alone.</summary>
        private bool Changed(out byte[] edited)
        {
            edited = ToArray();
            return !edited.AsSpan().SequenceEqual(_original);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_stored)
            {
                _stored = true;
                if (Changed(out var edited)) await _store(edited, CancellationToken.None);
            }
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_stored)
            {
                _stored = true;
                if (Changed(out var edited)) _store(edited, CancellationToken.None).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>A declared forward as a run's or chat's environment answers it: <c>127.0.0.1:LocalPort</c> reaches <c>Host:Port</c> through the egress proxy.</summary>
public sealed record RunForward(int LocalPort, string Host, int Port);
