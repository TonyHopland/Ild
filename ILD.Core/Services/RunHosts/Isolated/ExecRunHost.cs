using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
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

/// <summary>
/// What the run hosts that put each loop run in an environment of its own (a Kubernetes pod, a container) share: the
/// agent's processes are commands exec'd there, its files are read and written the same way, and code goes in and out
/// as bundles, so the environment never holds the git credential or the mirror. A subclass supplies the exec
/// transport and the lifecycle of the environment: how it is made, found, deleted and swept.
/// </summary>
public abstract class ExecRunHost(IProcessRunner runner, ILogger? logger) : IRunHost
{
    // Each command is a process group of its own, recorded under a name, so it can be killed as a group
    // without a connection to the command: the runtime does not reliably kill what an exec ran when its stream closes.
    private const string CommandWrapper = """
        dir=$1; id=$2; shift 2
        cd -- "$dir" || exit 126
        mkdir -p -- "/run/ild/procs/${id%/*}"
        exec setsid -w sh -c 'f=/run/ild/procs/$1; shift; echo $$ > "$f"; "$@"; rc=$?; rm -f -- "$f"; exit $rc' sh "$id" "$@"
        """;

    private const string KillCommandScript = """
        f=/run/ild/procs/$1
        [ -f "$f" ] || exit 0
        pid=$(cat -- "$f"); rm -f -- "$f"
        case "$pid" in ''|*[!0-9]*) exit 0 ;; esac
        kill -s TERM -- "-$pid" 2>/dev/null
        i=0
        while kill -0 -- "-$pid" 2>/dev/null && [ "$i" -lt 50 ]; do sleep 0.1; i=$((i+1)); done
        kill -s KILL -- "-$pid" 2>/dev/null
        exit 0
        """;

    internal const string KillAllScript = """
        n=0; pids=
        for f in "/run/ild/procs/$1"/*; do
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

    private static readonly Regex StagedPath = new(@"[A-Za-z0-9._/\-]*", RegexOptions.Compiled);

    /// <summary>
    /// Makes the run's or chat's environment answer the declared forwards (docs/adr/0020) on its own loopback, as ILD
    /// does under <c>dotnet run</c>: one socat per forward listens on <c>127.0.0.1:&lt;local port&gt;</c> and tunnels each
    /// connection to the destination through ILD's egress proxy with <c>CONNECT</c>, so the proxy judges and logs it
    /// as it does every other. Idempotent: a relay that is running for the same destination is left alone, one for a
    /// changed or deleted forward is stopped. Arguments: the proxy's host and port, then local port, host and port
    /// per forward.
    /// </summary>
    internal const string ForwardsScript = """
        dir="${ILD_FORWARDS_DIR:-/tmp/ild-forwards}"; mkdir -p "$dir"
        proxy_host=$1; proxy_port=$2; shift 2
        want=" "
        while [ "$#" -ge 3 ]; do
            local_port=$1; spec="$2:$3"; shift 3
            want="$want$local_port "
            if [ -f "$dir/$local_port.pid" ] && kill -0 "$(cat "$dir/$local_port.pid")" 2>/dev/null \
                && [ "$(cat "$dir/$local_port.spec" 2>/dev/null)" = "$spec" ]; then
                continue
            fi
            [ -f "$dir/$local_port.pid" ] && kill "$(cat "$dir/$local_port.pid")" 2>/dev/null
            setsid socat "TCP-LISTEN:$local_port,bind=127.0.0.1,reuseaddr,fork" \
                "PROXY:$proxy_host:$spec,proxyport=$proxy_port" </dev/null >/dev/null 2>&1 &
            echo "$!" > "$dir/$local_port.pid"; echo "$spec" > "$dir/$local_port.spec"
        done
        for f in "$dir"/*.pid; do
            [ -e "$f" ] || continue
            local_port=$(basename "$f" .pid)
            case "$want" in *" $local_port "*) ;; *) kill "$(cat "$f")" 2>/dev/null; rm -f "$f" "$dir/$local_port.spec" ;; esac
        done
        exit 0
        """;

    // A forward's host goes into a socat address, where a comma or a colon would be read as an option or a field.
    private static readonly Regex ForwardHost = new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    /// <summary>The declared forwards, read when a command or terminal starts in a run or chat; null runs with none.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<RunForward>>>? Forwards { get; set; }

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
            foreach (var f in forwards)
                arguments.AddRange([f.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Host, f.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            var result = await RunScriptAsync(owner, ForwardsScript, arguments, ct);
            if (result.ExitCode != 0)
                Logger?.LogWarning("Could not start the forwards in the {Noun} of {Owner}: {Error}", Noun, owner.Id, result.StdErr.Trim());
            if (forwards.Count > 0) _forwarded[owner] = true; else _forwarded.TryRemove(owner, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex, "Could not start the forwards in the {Noun} of {Owner}", Noun, owner.Id);
        }
    }

    private protected readonly ILogger? Logger = logger;

    /// <summary>The directory of ILD's that holds the files a launch is given to read.</summary>
    internal Func<string> AgentReadRoot { get; init; } = () => AgentIsolation.AgentReadRoot;

    /// <summary>The runs whose environments are kept whatever the caller of <see cref="ListLeftoversAsync"/> says is live: those that have not ended, a failed one included, since it can be retried in the environment it has.</summary>
    internal Func<CancellationToken, Task<IReadOnlySet<Guid>>> RetainedRuns { get; init; } = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public abstract RunHostKind Kind { get; }

    /// <summary>The <see cref="RunLeftover.Kind"/> of what this host's sweep finds.</summary>
    public abstract string LeftoverKind { get; }

    public abstract TimeSpan SweepInterval { get; }

    /// <summary>What the environment is called in messages: "pod" or "container".</summary>
    protected abstract string Noun { get; }

    /// <summary>ILD's address as the run reaches it: where the model gateway and the MCP endpoint are.</summary>
    protected abstract string IldUrl { get; }

    /// <summary>The port of ILD's egress proxy; 0 when there is none.</summary>
    protected abstract int EgressProxyPort { get; }

    /// <summary>Whether this host keeps chats: whether a path under <see cref="RunPaths.ChatsRoot"/> is one of its own.</summary>
    public virtual bool HostsChats => false;

    /// <summary>Start a command in the run's environment, with the streams it needs.</summary>
    protected abstract Task<IExecStream> OpenExecStreamAsync(ExecOwner owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct);

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

    /// <summary>
    /// Install the toolchains and agents a run needs into the shared store through <paramref name="installer"/>, a
    /// container or pod of the runner image that mounts the store writable, and return what the run's commands get.
    /// </summary>
    protected async Task<RunToolEnvironment> InstallToolsAsync(Guid installer, IReadOnlyList<RunTool> tools, IReadOnlyList<RunAgent> agents, CancellationToken ct)
    {
        // The script goes in on stdin, read whole before it runs: as an argument it would be part of the exec's URL on
        // Kubernetes, which a long script makes too long for the API server (414).
        using var script = new MemoryStream(Encoding.UTF8.GetBytes(RunToolStore.InstallScript));
        var result = await ExecAsync(installer, [.. ScriptFromStdin, .. RunToolStore.Arguments(tools, agents)], script, null, ct);
        if (!result.Success)
        {
            var asked = string.Join(", ", tools.Select(t => t.ToString()).Concat(agents.Select(a => $"{a.Agent.Key}@{a.Version}")));
            var tail = string.Join('\n', result.StdErr.Trim().Split('\n').TakeLast(15));
            throw new InvalidOperationException($"could not install the run's toolchains ({asked}) in the toolchain store: {tail}");
        }
        return RunToolStore.Parse(result.StdOut);
    }

    /// <summary>Make the environment ready to work in: the run's home and the code the base repository has.</summary>
    protected async Task PrepareAsync(Guid owner, RunEnvironmentRequest request, CancellationToken ct)
    {
        var home = await RunScriptAsync(owner, HomeScript, [RunPaths.HomeDirectory(owner)], ct);
        if (!home.Success)
            throw new InvalidOperationException($"could not make the run's home in its {Noun}: {home.StdErr.Trim()}");
        await PopulateAsync(owner, request, RunPaths.WorkingDirectory(owner), ct);
    }

    // The base repository's branches come in as a bundle, the same way a bundle from the mirror does: the run holds
    // only what the bundle carries, and its origin is the remote.
    private async Task PopulateAsync(Guid owner, RunEnvironmentRequest request, string workingDirectory, CancellationToken ct)
    {
        var bundlePath = Path.Combine(Path.GetTempPath(), $"ild-run-{Guid.NewGuid():N}.bundle");
        try
        {
            await using (var sink = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write))
            {
                var created = await runner.RunAsync(
                    "git", ["-C", request.BasePath, "bundle", "create", "--quiet", "-", "--all"], "/", ct, null,
                    new ProcessRunOptions(StandardOutputTo: sink));
                if (!created.Success)
                    throw new InvalidOperationException($"could not bundle {request.BasePath} for the run's {Noun}: {created.StdErr.Trim()}");
            }

            var origin = await runner.RunAsync("git", ["-C", request.BasePath, "config", "--get", "remote.origin.url"], "/", ct);
            var originUrl = origin.Success ? RepositoryManager.WithoutUserInfo(origin.StdOut.Trim()) : request.BasePath;

            await using var bundle = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var pushed = await PushBundleAsync(workingDirectory, originUrl, bundle, ct);
            if (!pushed.Success)
                throw new InvalidOperationException($"could not put the code in the run's {Noun}: {pushed.StdErr.Trim()}");
        }
        finally
        {
            File.Delete(bundlePath);
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
        var owner = RunPaths.ChatOf(psi.WorkingDirectory) is { } chat && HostsChats
            ? new ExecOwner(chat, Chat: true)
            : command.OwnerId != Guid.Empty ? command.OwnerId : RunPaths.OwnerOf(psi.WorkingDirectory)
                ?? throw new InvalidOperationException($"a command for a {Noun} needs the run it belongs to");

        await SyncForwardsAsync(owner, ct);
        var (fileName, arguments) = (psi.FileName, psi.ArgumentList.ToList());
        var environment = EnvironmentFor(command, psi, owner, out var network);
        if (owner.Chat)
            foreach (var line in await ChatEnvironmentAsync(owner.Id, ct))
                environment[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
        if (command.AsAgent)
        {
            if (Path.IsPathRooted(fileName)) fileName = Path.GetFileName(fileName);
            (arguments, environment) = await StageAgentFilesAsync(owner, arguments, environment, ct);
        }
        foreach (var (name, value) in network) environment[name] = value;

        var directory = string.IsNullOrEmpty(psi.WorkingDirectory) ? RunPaths.WorkingDirectory(owner.Id) : psi.WorkingDirectory;
        // Only a command started for the run itself is one KillAllAsync ends; a preview service, which has no owner, outlives a retry.
        var id = $"{(command.OwnerId != Guid.Empty ? command.OwnerId.ToString("N") : "unowned")}/{Guid.NewGuid():N}";
        var wrapped = new List<string> { "/bin/sh", "-c", CommandWrapper, "sh", directory, id, "env" };
        wrapped.AddRange(environment.Select(e => $"{e.Key}={e.Value}"));
        wrapped.Add(fileName);
        wrapped.AddRange(arguments);

        var exec = await OpenExecAsync(owner, wrapped, stdin: psi.RedirectStandardInput, tty: false, ct);
        return new ExecRunProcess(exec, Noun, id, psi.RedirectStandardInput, (commandId, token) => KillCommandAsync(owner, commandId, token));
    }

    /// <summary>
    /// What the command needs set in the run: what its caller put on it that the orchestrator's own environment did not
    /// already hold (a start info begins as a copy of that environment, none of which belongs in the run), with the
    /// orchestrator's secrets taken out, and the run's own home.
    /// </summary>
    private Dictionary<string, string> EnvironmentFor(RunCommand command, ProcessStartInfo psi, ExecOwner owner, out Dictionary<string, string> network)
    {
        var own = Environment.GetEnvironmentVariables();
        var secrets = AgentIsolation.SecretEnvironmentKeys;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in psi.Environment)
        {
            if (value is null || secrets.Contains(name)) continue;
            if (own.Contains(name) && (string?)own[name] == value) continue;
            // ILD's directories mean nothing in the run, whose PATH is its toolchains', set when it was made.
            if (name == "PATH") continue;
            environment[name] = value;
        }
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string>())
            environment[name] = value;

        network = new Dictionary<string, string>(StringComparer.Ordinal);
        if (command.AsAgent)
        {
            // The run's network reaches only ILD, so even a launch in network mode Off goes through the proxy,
            // which lets every request of that mode through.
            if (AgentIsolation.EgressProxyUrl(command.ProviderId) is { } proxy)
                foreach (var (name, value) in AgentIsolation.EgressProxyEnvironment(proxy))
                    network[name] = value;
        }

        var ild = new Uri(IldUrl).Host;
        foreach (var bag in new[] { environment, network })
            foreach (var (name, value) in bag.ToList())
                bag[name] = ThroughIldHost(value, ild);

        environment["HOME"] = owner.Home;
        return environment;
    }

    // The proxy ILD points a launch at listens on ILD's loopback; a run reaches it at ILD's address.
    private string ThroughIldHost(string value, string ildHost)
        => EgressProxyPort > 0
            ? value.Replace($"{IPAddress.Loopback}:{EgressProxyPort}", $"{ildHost}:{EgressProxyPort}", StringComparison.Ordinal)
            : value;

    // The files a launch is given to read (MCP configs, package feed settings, an agent extension) sit in a directory
    // of ILD's that a run cannot see; they go to memory in it and the launch is pointed at the copies.
    private async Task<(List<string> Arguments, Dictionary<string, string> Environment)> StageAgentFilesAsync(
        ExecOwner owner, List<string> arguments, Dictionary<string, string> environment, CancellationToken ct)
    {
        string root;
        try { root = AgentReadRoot(); }
        catch (InvalidOperationException) { return (arguments, environment); }

        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        string Rewrite(string text)
        {
            var from = 0;
            while (text.IndexOf(root, from, StringComparison.Ordinal) is var at and >= 0)
            {
                var end = at + root.Length;
                if (end < text.Length && text[end] == '/')
                {
                    var tail = StagedPath.Match(text, end).Value;
                    staged[root + tail] = RunPaths.AgentReadDirectory + tail;
                }
                from = end;
            }
            return text.Replace(root, RunPaths.AgentReadDirectory, StringComparison.Ordinal);
        }

        var rewrittenArguments = arguments.Select(Rewrite).ToList();
        var rewrittenEnvironment = environment.ToDictionary(e => e.Key, e => Rewrite(e.Value), StringComparer.Ordinal);
        foreach (var (local, remote) in staged)
            await CopyIntoRunAsync(owner, local, remote, ct);
        return (rewrittenArguments, rewrittenEnvironment);
    }

    private async Task CopyIntoRunAsync(ExecOwner owner, string local, string remote, CancellationToken ct)
    {
        if (File.Exists(local))
        {
            await using var content = File.OpenRead(local);
            await WriteFileInRunAsync(owner, remote, content, readOnly: true, ct);
        }
        else if (Directory.Exists(local))
        {
            foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                await CopyIntoRunAsync(owner, file, remote + file[local.Length..], ct);
        }
    }

    private async Task<bool> KillCommandAsync(ExecOwner owner, string commandId, CancellationToken ct)
        => (await RunScriptAsync(owner, KillCommandScript, [commandId], ct)).Success;

    // The id is a run's or a chat's; a run has an environment of its own and a chat does not, so a run is the one with a pod or container.
    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
    {
        ExecOwner owner = ownerId;
        if (!await RunExistsAsync(owner, ct))
        {
            owner = new ExecOwner(ownerId, Chat: true);
            if (!HostsChats || !await RunExistsAsync(owner, ct)) return 0;
        }
        var result = await RunScriptAsync(owner, KillAllScript, [ownerId.ToString("N")], ct);
        return result.Success && int.TryParse(result.StdOut.Trim(), out var killed) ? killed : 0;
    }

    // -- Terminal ----------------------------------------------------------------

    public async Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default)
    {
        var owner = RunPaths.OwnerOf(options.Cwd)
            ?? throw new InvalidOperationException($"a terminal for a {Noun} needs a working directory in it");
        var environment = (options.Environment ?? new Dictionary<string, string>()).ToDictionary(e => e.Key, e => e.Value);
        environment["HOME"] = RunPaths.HomeDirectory(owner);
        environment.TryAdd("TERM", "xterm-256color");
        await SyncForwardsAsync(owner, ct);

        var command = new List<string> { "/bin/sh", "-c", "cd -- \"$1\" && shift && exec \"$@\"", "sh", options.Cwd, "env" };
        command.AddRange(environment.Select(e => $"{e.Key}={e.Value}"));
        command.Add(options.App);
        command.AddRange(options.CommandLine ?? []);

        var exec = await OpenExecAsync(owner, command, stdin: true, tty: true, ct);
        var pty = new ExecPty(exec);
        pty.Resize(options.Cols, options.Rows);
        return pty;
    }

    public AgentIsolation.AgentCommand RouteTerminalCommand(string fileName, IReadOnlyList<string> arguments, Guid? providerId, ILD.Data.Enums.NetworkMode mode)
        => throw new NotSupportedException("the provider sign-in terminal runs on the local run host");

    // -- Bundles -----------------------------------------------------------------

    public async Task<RunHostResult> PushBundleAsync(string targetPath, string originUrl, Stream bundle, CancellationToken ct = default)
        => ToResult(await ExecAsync(OwnerFor(targetPath), ["/bin/sh", "-c", Local.LocalRunHost.CloneScript, "sh", targetPath, originUrl], bundle, null, ct));

    public async Task<RunHostResult> FetchBundleAsync(string clonePath, Stream bundle, CancellationToken ct = default)
        => ToResult(await ExecAsync(OwnerFor(clonePath), ["/bin/sh", "-c", Local.LocalRunHost.FetchBundleScript, "sh", clonePath], bundle, null, ct));

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

    public Task<RunHostOutput> RunToolAsync(
        string runPath, string fileName, IReadOnlyList<string> arguments, RunToolStreams? streams = null, CancellationToken ct = default)
        => ExecAsync(OwnerFor(runPath), [fileName, .. arguments], streams?.StandardInputFrom, streams?.StandardOutputTo, ct);

    // -- Files -------------------------------------------------------------------

    public string? ReadTextFile(string path)
    {
        var result = RunScriptAsync(OwnerFor(path), "[ -f \"$1\" ] || exit 3; cat -- \"$1\"", [path], CancellationToken.None).GetAwaiter().GetResult();
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

    private async Task<MemoryStream?> ReadAllAsync(string path, CancellationToken ct)
    {
        var content = new MemoryStream();
        var result = await ExecAsync(OwnerFor(path), ["/bin/sh", "-c", "[ -f \"$1\" ] || exit 3; exec cat -- \"$1\"", "sh", path], null, content, ct);
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
        var content = new MemoryStream();
        var result = await ExecAsync(
            OwnerFor(path), ["/bin/sh", "-c", "[ -f \"$1\" ] || exit 3; tail -c \"+$(( $2 + 1 ))\" -- \"$1\" | head -c \"$3\"", "sh", path, offset.ToString(), count.ToString()],
            null, content, ct);
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
            ["/bin/sh", "-c", "mkdir -p -- \"$(dirname -- \"$1\")\" && rm -f -- \"$1\" && cat > \"$1\" && { [ \"$2\" != ro ] || chmod 0444 -- \"$1\"; }", "sh", path, readOnly ? "ro" : "rw"],
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

    public string GatewayApiUrl(string? configuredApiUrl) => IldUrl;

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

    /// <summary>What the commands of a run or chat get from the toolchain store for <paramref name="plan"/>, installed into it first when it lacks them; null when there is no store.</summary>
    protected abstract Task<RunToolEnvironment?> ToolsForAsync(string label, RunToolPlan plan, CancellationToken ct);

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

        var plan = RunToolDetector.Resolve(basePath);
        var known = ChatToolsOf(chat);
        var added = false;
        lock (known)
        {
            foreach (var tool in plan.Tools)
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
        var tooling = await ToolsForAsync($"Chat {chat}", new RunToolPlan(tools, tools.Select(t => t.ToString()).ToList()), ct);
        return tooling?.For(RunPaths.ChatHome(chat)) ?? [];
    }

    /// <summary>The chat's folder was deleted: forget what was kept about it.</summary>
    private void ChatRemoved(Guid chat)
    {
        _chatTools.TryRemove(chat, out _);
        try { File.Delete(ChatToolsFile(chat)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }


    public async Task<string> CreateChatFolderAsync(Guid chatId, string localRoot, CancellationToken ct = default)
    {
        if (!HostsChats) throw new NotSupportedException($"this host does not keep chats in a {Noun}");
        var home = await RunScriptAsync(new ExecOwner(chatId, Chat: true), HomeScript, [RunPaths.ChatHome(chatId)], ct);
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

    // -- Exec --------------------------------------------------------------------

    protected ExecOwner OwnerFor(string path)
        => RunPaths.OwnerOf(path) is { } run ? run
            : RunPaths.ChatOf(path) is { } chat && HostsChats ? new ExecOwner(chat, Chat: true)
            : throw new InvalidOperationException($"{path} is not a path in a run's or chat's {Noun}");

    private protected async Task<IExecStream> OpenExecAsync(ExecOwner owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        try
        {
            return await OpenExecStreamAsync(owner, command, stdin, tty, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"could not run a command in the {Noun} of {(owner.Chat ? "chat" : "run")} {owner.Id:N}: {ex.Message}", ex);
        }
    }

    // Reads a script from stdin into a file, then runs it with the arguments, so what the script runs does not read
    // the rest of the script as its own input.
    private static readonly string[] ScriptFromStdin =
        ["/bin/bash", "-c", "f=$(mktemp) && cat > \"$f\" && exec bash \"$f\" \"$@\"", "bash"];

    protected Task<RunHostOutput> RunScriptAsync(ExecOwner owner, string script, IReadOnlyList<string> arguments, CancellationToken ct)
        => ExecAsync(owner, ["/bin/bash", "-c", script, "bash", .. arguments], null, null, ct);

    /// <summary>
    /// A command in the run, to its end. <paramref name="stdin"/> is sent and closed; <paramref name="stdout"/> receives
    /// the output when given, otherwise it is collected as text.
    /// </summary>
    private async Task<RunHostOutput> ExecAsync(ExecOwner owner, IReadOnlyList<string> command, Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        using var exec = await OpenExecAsync(owner, command, stdin is not null, tty: false, ct);
        using var collected = new MemoryStream();
        using var errors = new MemoryStream();
        var output = Drain(exec.Stdout, stdout ?? collected, ct);
        var failures = Drain(exec.Stderr!, errors, ct);

        if (stdin is not null)
        {
            try
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stdin.ReadAsync(buffer, ct)) > 0)
                    await exec.WriteStdinAsync(buffer.AsMemory(0, read), ct);
                await exec.CloseStdinAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
            {
                // The command ended without reading it all; its status says how.
            }
        }

        var status = await exec.Status.WaitAsync(ct);
        await Task.WhenAll(output, failures);
        var stderr = Encoding.UTF8.GetString(errors.ToArray());
        if (status is null)
            stderr += $"the connection to the run's {Noun} ended before the command did\n";
        return new RunHostOutput(status ?? ExecRunProcess.ConnectionLostExitCode, Encoding.UTF8.GetString(collected.ToArray()), stderr);
    }

    private static async Task Drain(Stream from, Stream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
    }

    private sealed class WriteBackStream : MemoryStream
    {
        private readonly Func<byte[], CancellationToken, Task> _store;
        private bool _stored;

        public WriteBackStream(byte[] content, Func<byte[], CancellationToken, Task> store)
        {
            _store = store;
            Write(content);
            Position = 0;
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_stored)
            {
                _stored = true;
                await _store(ToArray(), CancellationToken.None);
            }
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_stored)
            {
                _stored = true;
                _store(ToArray(), CancellationToken.None).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>A declared forward as a run's or chat's environment answers it: <c>127.0.0.1:LocalPort</c> reaches <c>Host:Port</c> through the egress proxy.</summary>
public sealed record RunForward(int LocalPort, string Host, int Port);
