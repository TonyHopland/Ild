using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// The run host that puts each loop run in a pod of its own, with a volume of its own: the agent's processes are
/// commands exec'd in the pod, its files are read and written the same way, and code goes in and out as bundles, so
/// the pod never holds the git credential or the mirror. The run's Secret owns the pod, the volume claim and the
/// network policy, so deleting the Secret deletes them all.
/// </summary>
public sealed class KubernetesRunHost : IRunHost
{
    public const string RunLeftoverKind = "kubernetes-run";

    // Each command is a process group of its own, recorded under a name, so it can be killed as a group
    // without a connection to the command: the API does not reliably kill what an exec ran when its stream closes.
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

    private const int NotThereExitCode = 3;

    private static readonly Regex StagedPath = new(@"[A-Za-z0-9._/\-]*", RegexOptions.Compiled);

    private readonly IKubernetes _client;
    private readonly KubernetesRunHostOptions _options;
    private readonly string _namespace;
    private readonly KubernetesManifests _manifests;
    private readonly IProcessRunner _runner;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<Guid, string> _podAddresses = new();

    public KubernetesRunHost(
        IKubernetes client, KubernetesRunHostOptions options, string namespaceName, IProcessRunner runner, ILogger? logger = null)
    {
        _client = client;
        _options = options;
        _namespace = namespaceName;
        _manifests = new KubernetesManifests(options, namespaceName);
        _runner = runner;
        _logger = logger;
    }

    /// <summary>How often a starting pod is looked at.</summary>
    internal TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The directory of ILD's that holds the files a launch is given to read.</summary>
    internal Func<string> AgentReadRoot { get; init; } = () => AgentIsolation.AgentReadRoot;

    public RunHostKind Kind => RunHostKind.Kubernetes;

    /// <summary>The runs whose pods are kept whatever the caller of <see cref="ListLeftoversAsync"/> says is live: those that have not ended, a failed one included, since it can be retried in the pod it has.</summary>
    internal Func<CancellationToken, Task<IReadOnlySet<Guid>>> RetainedRuns { get; init; } = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public TimeSpan SweepInterval => _options.SweepInterval;

    // -- Lifecycle ---------------------------------------------------------------

    public async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var owner = request.OwnerId;
        var workingDirectory = KubernetesNames.WorkingDirectory(owner);
        if (await IsHealthyAsync(owner, ct))
            return new RunEnvironment(owner, workingDirectory);

        var secret = await CreateOwnerAsync(owner, ct);
        try
        {
            var reference = KubernetesManifests.OwnerReference(secret);
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(_manifests.Claim(owner, reference), _namespace, cancellationToken: ct));
            await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.Policy(owner, reference), _namespace, cancellationToken: ct));
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPodAsync(_manifests.Pod(owner, reference), _namespace, cancellationToken: ct));
            var pod = await WaitReadyAsync(owner, ct);
            _podAddresses[owner] = pod.Status.PodIP;

            await RunScriptAsync(owner, "mkdir -p -- \"$1\"", [KubernetesNames.HomeDirectory(owner)], ct);
            await PopulateAsync(owner, request, workingDirectory, ct);
        }
        catch
        {
            await DeleteOwnerQuietlyAsync(owner);
            throw;
        }

        return new RunEnvironment(owner, workingDirectory);
    }

    public async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        _podAddresses.TryRemove(environment.OwnerId, out _);
        await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedSecretAsync(
            KubernetesNames.ObjectName(environment.OwnerId), _namespace, new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: ct));
    }

    private async Task<bool> IsHealthyAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            if (!IsReady(pod)) return false;
            _podAddresses[owner] = pod.Status.PodIP;
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<V1Secret> CreateOwnerAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            return await _client.CoreV1.CreateNamespacedSecretAsync(_manifests.Secret(owner), _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return await _client.CoreV1.ReadNamespacedSecretAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
        }
    }

    private static async Task CreateIfMissingAsync<T>(Func<Task<T>> create)
    {
        try { await create(); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict) { }
    }

    private static async Task IgnoreNotFoundAsync<T>(Func<Task<T>> call)
    {
        try { await call(); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    private async Task DeleteOwnerQuietlyAsync(Guid owner)
    {
        try { await DeleteAsync(new RunEnvironment(owner, KubernetesNames.WorkingDirectory(owner)), CancellationToken.None); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not remove the objects of run {Run} after its pod failed to start; the next sweep removes them", owner); }
    }

    private static bool IsReady(V1Pod pod)
        => pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status.PodIP)
            && pod.Status.ContainerStatuses?.All(c => c.Ready) == true;

    private async Task<V1Pod> WaitReadyAsync(Guid owner, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            if (IsReady(pod)) return pod;
            if (pod.Status?.Phase is "Failed" or "Succeeded")
                throw new InvalidOperationException($"the pod of run {owner:N} ended before it was ready: {WhyNotReady(pod)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the pod of run {owner:N} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(pod)}");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    private static string WhyNotReady(V1Pod pod)
    {
        var condition = pod.Status?.Conditions?.FirstOrDefault(c => c.Status == "False" && !string.IsNullOrEmpty(c.Message));
        return condition?.Message ?? pod.Status?.Reason ?? pod.Status?.Phase ?? "no status";
    }

    // The base repository's branches come in as a bundle, the same way a bundle from the mirror does: the pod holds
    // only what the bundle carries, and its origin is the remote.
    private async Task PopulateAsync(Guid owner, RunEnvironmentRequest request, string workingDirectory, CancellationToken ct)
    {
        var bundlePath = Path.Combine(Path.GetTempPath(), $"ild-run-{Guid.NewGuid():N}.bundle");
        try
        {
            await using (var sink = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write))
            {
                var created = await _runner.RunAsync(
                    "git", ["-C", request.BasePath, "bundle", "create", "--quiet", "-", "--all"], "/", ct, null,
                    new ProcessRunOptions(StandardOutputTo: sink));
                if (!created.Success)
                    throw new InvalidOperationException($"could not bundle {request.BasePath} for the run's pod: {created.StdErr.Trim()}");
            }

            var origin = await _runner.RunAsync("git", ["-C", request.BasePath, "config", "--get", "remote.origin.url"], "/", ct);
            var originUrl = origin.Success ? RepositoryManager.WithoutUserInfo(origin.StdOut.Trim()) : request.BasePath;

            await using var bundle = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var pushed = await PushBundleAsync(workingDirectory, originUrl, bundle, ct);
            if (!pushed.Success)
                throw new InvalidOperationException($"could not put the code in the run's pod: {pushed.StdErr.Trim()}");
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
            throw new InvalidOperationException($"could not check out {request.BranchName} in the run's pod: {checkout.StdErr.Trim()}");
    }

    // -- Commands ----------------------------------------------------------------

    public async Task<IRunProcess> StartCommandAsync(RunCommand command, CancellationToken ct = default)
    {
        var psi = command.Start;
        var owner = command.OwnerId != Guid.Empty ? command.OwnerId : KubernetesNames.OwnerOf(psi.WorkingDirectory)
            ?? throw new InvalidOperationException("a command for a pod needs the run it belongs to");

        var (fileName, arguments) = command.AsAgent
            ? (psi.FileName, psi.ArgumentList.ToList())
            : AgentIsolation.Unrouted(psi) is { } inner ? (inner.FileName, inner.Arguments.ToList()) : (psi.FileName, psi.ArgumentList.ToList());
        var environment = EnvironmentFor(command, psi, owner, out var network);
        if (command.AsAgent)
        {
            if (Path.IsPathRooted(fileName)) fileName = Path.GetFileName(fileName);
            (arguments, environment) = await StageAgentFilesAsync(owner, arguments, environment, ct);
        }
        foreach (var (name, value) in network) environment[name] = value;

        var directory = string.IsNullOrEmpty(psi.WorkingDirectory) ? KubernetesNames.WorkingDirectory(owner) : psi.WorkingDirectory;
        // Only a command started for the run itself is one KillAllAsync ends; a preview service, which has no owner, outlives a retry.
        var id = $"{(command.OwnerId != Guid.Empty ? command.OwnerId.ToString("N") : "unowned")}/{Guid.NewGuid():N}";
        var wrapped = new List<string> { "/bin/sh", "-c", CommandWrapper, "sh", directory, id, "env" };
        wrapped.AddRange(environment.Select(e => $"{e.Key}={e.Value}"));
        wrapped.Add(fileName);
        wrapped.AddRange(arguments);

        var exec = await OpenExecAsync(owner, wrapped, stdin: psi.RedirectStandardInput, tty: false, ct);
        return new KubernetesRunProcess(exec, id, psi.RedirectStandardInput, (commandId, token) => KillCommandAsync(owner, commandId, token));
    }

    /// <summary>
    /// What the command needs set in the pod: what its caller put on it that the orchestrator's own environment did not
    /// already hold (a start info begins as a copy of that environment, none of which belongs in the pod), with the
    /// orchestrator's secrets taken out, and the pod's own home.
    /// </summary>
    private Dictionary<string, string> EnvironmentFor(RunCommand command, ProcessStartInfo psi, Guid owner, out Dictionary<string, string> network)
    {
        var own = Environment.GetEnvironmentVariables();
        var secrets = AgentIsolation.SecretEnvironmentKeys;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in psi.Environment)
        {
            if (value is null || secrets.Contains(name)) continue;
            if (own.Contains(name) && (string?)own[name] == value) continue;
            environment[name] = value;
        }
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string>())
            environment[name] = value;

        network = new Dictionary<string, string>(StringComparer.Ordinal);
        if (command.AsAgent)
        {
            var (proxy, _) = AgentIsolation.ResolveLaunchNetwork(command.NetworkMode, command.ProviderId);
            if (proxy is not null)
                foreach (var (name, value) in AgentIsolation.EgressProxyEnvironment(proxy))
                    network[name] = value;
        }

        var ild = new Uri(_options.IldUrl).Host;
        foreach (var bag in new[] { environment, network })
            foreach (var (name, value) in bag.ToList())
                bag[name] = ThroughIldHost(value, ild);

        environment["HOME"] = KubernetesNames.HomeDirectory(owner);
        return environment;
    }

    // The proxy ILD points a launch at listens on ILD's loopback; a pod reaches it at ILD's address.
    private string ThroughIldHost(string value, string ildHost)
        => _options.EgressProxyPort > 0
            ? value.Replace($"{IPAddress.Loopback}:{_options.EgressProxyPort}", $"{ildHost}:{_options.EgressProxyPort}", StringComparison.Ordinal)
            : value;

    // The files a launch is given to read (MCP configs, package feed settings, an agent extension) sit in a directory
    // of ILD's that a pod cannot see; they go to memory in the pod and the launch is pointed at the copies.
    private async Task<(List<string> Arguments, Dictionary<string, string> Environment)> StageAgentFilesAsync(
        Guid owner, List<string> arguments, Dictionary<string, string> environment, CancellationToken ct)
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
                    staged[root + tail] = KubernetesNames.AgentReadDirectory + tail;
                }
                from = end;
            }
            return text.Replace(root, KubernetesNames.AgentReadDirectory, StringComparison.Ordinal);
        }

        var rewrittenArguments = arguments.Select(Rewrite).ToList();
        var rewrittenEnvironment = environment.ToDictionary(e => e.Key, e => Rewrite(e.Value), StringComparer.Ordinal);
        foreach (var (local, remote) in staged)
            await CopyIntoPodAsync(owner, local, remote, ct);
        return (rewrittenArguments, rewrittenEnvironment);
    }

    private async Task CopyIntoPodAsync(Guid owner, string local, string remote, CancellationToken ct)
    {
        if (File.Exists(local))
        {
            await using var content = File.OpenRead(local);
            await WriteFileInPodAsync(owner, remote, content, readOnly: true, ct);
        }
        else if (Directory.Exists(local))
        {
            foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                await CopyIntoPodAsync(owner, file, remote + file[local.Length..], ct);
        }
    }

    private async Task<bool> KillCommandAsync(Guid owner, string commandId, CancellationToken ct)
        => (await RunScriptAsync(owner, KillCommandScript, [commandId], ct)).Success;

    public async Task<int> KillAllAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (!await PodExistsAsync(ownerId, ct)) return 0;
        var result = await RunScriptAsync(ownerId, KillAllScript, [ownerId.ToString("N")], ct);
        return result.Success && int.TryParse(result.StdOut.Trim(), out var killed) ? killed : 0;
    }

    private async Task<bool> PodExistsAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // -- Terminal ----------------------------------------------------------------

    public async Task<IPtyConnection> OpenTerminalAsync(PtyOptions options, CancellationToken ct = default)
    {
        var owner = KubernetesNames.OwnerOf(options.Cwd)
            ?? throw new InvalidOperationException("a terminal for a pod needs a working directory in it");
        var environment = (options.Environment ?? new Dictionary<string, string>()).ToDictionary(e => e.Key, e => e.Value);
        environment["HOME"] = KubernetesNames.HomeDirectory(owner);
        environment.TryAdd("TERM", "xterm-256color");

        var command = new List<string> { "/bin/sh", "-c", "cd -- \"$1\" && shift && exec \"$@\"", "sh", options.Cwd, "env" };
        command.AddRange(environment.Select(e => $"{e.Key}={e.Value}"));
        command.Add(options.App);
        command.AddRange(options.CommandLine ?? []);

        var exec = await OpenExecAsync(owner, command, stdin: true, tty: true, ct);
        var pty = new KubernetesPty(exec);
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
        => Task.FromResult(KubernetesNames.OwnerOf(path) is not null);

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
        var content = await ReadAllAsync(path, ct) ?? throw new IOException($"{path} is not a file in the run's pod");
        return new WriteBackStream(content.ToArray(), (bytes, token) => WriteFileInPodAsync(OwnerFor(path), path, new MemoryStream(bytes), readOnly: false, token));
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
        await using var range = await ReadRangeAsync(path, offset, count) ?? throw new FileNotFoundException($"{path} is not a file in the run's pod", path);
        await consume(range);
    }

    public async Task WriteFileAsync(string path, Stream content, CancellationToken ct = default)
        => await WriteFileInPodAsync(OwnerFor(path), path, content, readOnly: false, ct);

    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await WriteFileInPodAsync(OwnerFor(path), path, bytes, readOnly: false, ct);
    }

    public async Task WriteReadOnlyFileAsync(string path, string content, CancellationToken ct = default)
    {
        await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await WriteFileInPodAsync(OwnerFor(path), path, bytes, readOnly: true, ct);
    }

    private async Task WriteFileInPodAsync(Guid owner, string path, Stream content, bool readOnly, CancellationToken ct)
    {
        var result = await ExecAsync(
            owner,
            ["/bin/sh", "-c", "mkdir -p -- \"$(dirname -- \"$1\")\" && rm -f -- \"$1\" && cat > \"$1\" && { [ \"$2\" != ro ] || chmod 0444 -- \"$1\"; }", "sh", path, readOnly ? "ro" : "rw"],
            content, null, ct);
        if (!result.Success)
            throw new IOException($"could not write {path} in the run's pod: {result.StdErr.Trim()}");
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var result = await RunScriptAsync(OwnerFor(path), "mkdir -p -- \"$1\"", [path], ct);
        if (!result.Success)
            throw new IOException($"could not create {path} in the run's pod: {result.StdErr.Trim()}");
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
        }
        return all;
    }

    // -- Preview and gateway -----------------------------------------------------

    public async Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
    {
        var owner = OwnerFor(workingDirectory);
        if (!_podAddresses.TryGetValue(owner, out var address))
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            address = pod.Status?.PodIP is { Length: > 0 } ip ? ip : throw new InvalidOperationException($"the pod of run {owner:N} has no address yet");
            _podAddresses[owner] = address;
        }
        return new UriBuilder(Uri.UriSchemeHttp, address, port).Uri;
    }

    public string? RunHomeFor(string workingDirectory)
        => KubernetesNames.OwnerOf(workingDirectory) is { } owner ? KubernetesNames.HomeDirectory(owner) : null;

    public string GatewayApiUrl(string? configuredApiUrl) => _options.IldUrl;

    // -- Sweep -------------------------------------------------------------------

    public async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
    {
        try
        {
            var retained = await RetainedRuns(ct);
            var selector = KubernetesNames.SelectorFor(_options.Instance);
            var runs = new HashSet<string>(StringComparer.Ordinal);
            void Collect(IEnumerable<IMetadata<V1ObjectMeta>> items)
            {
                foreach (var item in items)
                    if (item.Metadata.Labels is { } labels && labels.TryGetValue(KubernetesNames.RunLabel, out var run))
                        runs.Add(run);
            }

            Collect((await _client.CoreV1.ListNamespacedSecretAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);

            return runs
                .Where(run => !Guid.TryParseExact(run, "N", out var id) || !liveIds.Contains(id) && !retained.Contains(id))
                .Select(run => new RunLeftover(RunLeftoverKind, run))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            _logger?.LogWarning(ex, "Could not list the pods runs left in namespace {Namespace}; the next sweep tries again", _namespace);
            return [];
        }
    }

    public async Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
    {
        if (leftover.Kind != RunLeftoverKind)
            throw new ArgumentException($"unknown leftover kind '{leftover.Kind}'", nameof(leftover));
        if (!Guid.TryParseExact(leftover.Location, "N", out var owner))
            return false;

        var name = KubernetesNames.ObjectName(owner);
        var options = new V1DeleteOptions { PropagationPolicy = "Background" };
        try
        {
            // The Secret first, which takes the rest with it; each is also deleted by name for a leftover whose Secret is already gone.
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedSecretAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(name, _namespace, options, cancellationToken: ct));
            _podAddresses.TryRemove(owner, out _);
            return true;
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            _logger?.LogWarning(ex, "Could not delete the objects of run {Run}; the next sweep tries again", owner);
            return false;
        }
    }

    /// <summary>
    /// Warns when the volumes of runs would outlive them: a StorageClass whose reclaim policy is not <c>Delete</c>
    /// keeps the disk after its claim is gone. Says so rather than failing when the class cannot be read.
    /// </summary>
    public async Task CheckStorageAsync(CancellationToken ct = default)
    {
        try
        {
            var classes = (await _client.StorageV1.ListStorageClassAsync(cancellationToken: ct)).Items;
            var chosen = _options.StorageClass is { } name
                ? classes.FirstOrDefault(c => c.Metadata.Name == name)
                : classes.FirstOrDefault(c => c.Metadata.Annotations?.TryGetValue("storageclass.kubernetes.io/is-default-class", out var isDefault) == true && isDefault == "true");
            if (chosen is null)
            {
                _logger?.LogWarning("There is no {Class} StorageClass, so the volume of a run cannot be provisioned", _options.StorageClass ?? "default");
                return;
            }
            if (!string.Equals(chosen.ReclaimPolicy ?? "Delete", "Delete", StringComparison.Ordinal))
                _logger?.LogWarning(
                    "StorageClass {Class} has reclaimPolicy {Policy}: the volume of a run is kept after the run is removed. Use a class with reclaimPolicy Delete for runs",
                    chosen.Metadata.Name, chosen.ReclaimPolicy);
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            _logger?.LogInformation("Could not read the StorageClasses to check their reclaim policy ({Reason}); it needs read access to storageclasses", ex.Message);
        }
    }

    // -- Exec --------------------------------------------------------------------

    private static Guid OwnerFor(string path)
        => KubernetesNames.OwnerOf(path) ?? throw new InvalidOperationException($"{path} is not a path in a run's pod");

    private async Task<PodExec> OpenExecAsync(Guid owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        try
        {
            return await PodExec.OpenAsync(_client, _namespace, KubernetesNames.ObjectName(owner), command, stdin, tty, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"could not run a command in the pod of run {owner:N}: {ex.Message}", ex);
        }
    }

    private Task<RunHostOutput> RunScriptAsync(Guid owner, string script, IReadOnlyList<string> arguments, CancellationToken ct)
        => ExecAsync(owner, ["/bin/bash", "-c", script, "bash", .. arguments], null, null, ct);

    /// <summary>
    /// A command in the pod, to its end. <paramref name="stdin"/> is sent and closed; <paramref name="stdout"/> receives
    /// the output when given, otherwise it is collected as text.
    /// </summary>
    private async Task<RunHostOutput> ExecAsync(Guid owner, IReadOnlyList<string> command, Stream? stdin, Stream? stdout, CancellationToken ct)
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
            stderr += "the connection to the run's pod ended before the command did\n";
        return new RunHostOutput(status ?? KubernetesRunProcess.ConnectionLostExitCode, Encoding.UTF8.GetString(collected.ToArray()), stderr);
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
