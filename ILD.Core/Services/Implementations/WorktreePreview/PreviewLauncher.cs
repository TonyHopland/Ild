using ILD.Core.Services.Implementations.Storage;
using ILD.Data.DTOs;
using ILD.Core.Services.Interfaces;
using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Builds the environment of a preview command and starts it through the run host: the install steps and the
/// long-running services both go through here, so neither can drift from the other.
/// <paramref name="environment"/> is where our own <c>HOME</c> and <c>PATH</c> are read, and where an install puts
/// the npm global bin on <c>PATH</c>. <paramref name="credentials"/> is the credential <paramref name="egressProxy"/>
/// signs a launch under; with neither, no launch is proxied.
/// </summary>
internal sealed class PreviewLauncher(
    IRunWorkspace host,
    EgressProxyOptions? egressProxy,
    EgressCredentials? credentials,
    IProcessEnvironment environment,
    PreviewNotifier notifier,
    PreviewTimings timings,
    ILogger logger)
{
    private readonly IRunWorkspace _host = host;
    private readonly EgressProxyOptions? _egressProxy = egressProxy;
    private readonly EgressCredentials? _credentials = credentials;
    private readonly IProcessEnvironment _environment = environment;
    private readonly PreviewNotifier _notifier = notifier;
    private readonly PreviewTimings _timings = timings;
    private readonly ILogger _logger = logger;

    /// <summary>How much of an install command's output is kept; the rest is read and dropped.</summary>
    private readonly RunReadLimits _outputLimits = RunReadLimits.FromEnvironment();

    /// <summary>
    /// Per-worktree preview state the <em>preview itself</em> writes — the npm cache
    /// its install steps populate, and whatever a profile puts under
    /// <c>${STATE_DIR}</c>. Where the run host says, so the preview's steps can write
    /// it and see it where they run.
    /// </summary>
    public async Task<string> BuildStateDirectoryAsync(string worktreePath, CancellationToken cancellationToken)
    {
        RunInFor(worktreePath);
        var stateDirectory = _host.PreviewStateDirectoryFor(worktreePath);
        await _host.CreateDirectoryAsync(PreviewPaths.NpmCacheOf(stateDirectory), cancellationToken);
        return stateDirectory;
    }

    /// <param name="onProgress">
    /// Told which step is about to run (1-based index, total, the command as written in the
    /// config), and with null once the steps are done.
    /// </param>
    public async Task RunInstallStepsAsync(
        IReadOnlyList<PreviewCommandConfig> installSteps, PreviewRuntime runtime, NetworkMode mode, CancellationToken cancellationToken,
        Func<WorktreePreviewInstallProgress?, Task>? onProgress = null)
    {
        if (installSteps.Count == 0)
            return;

        var installLogPath = Path.Combine(runtime.LogDirectory, "install.log");
        using var feeds = runtime.Feeds.Acquire();
        var reported = false;
        for (var index = 0; index < installSteps.Count; index++)
        {
            var step = installSteps[index];
            if (string.IsNullOrWhiteSpace(step.Command))
                continue;

            if (onProgress != null)
            {
                reported = true;
                await onProgress(new WorktreePreviewInstallProgress { Index = index + 1, Total = installSteps.Count, Command = step.Command });
            }

            var resolved = await BuildResolvedStepAsync(step, runtime, null, feeds, mode, cancellationToken);
            var result = await RunCommandAsync(resolved, cancellationToken);

            var builder = new StringBuilder();
            builder.AppendLine($"> {resolved.Command}");
            if (!string.IsNullOrWhiteSpace(result.StdOut)) builder.AppendLine(result.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.StdErr)) builder.AppendLine(result.StdErr.TrimEnd());
            await using (var installLog = AppendLog.Open(runtime.LogDirectory, installLogPath))
            {
                var bytes = Encoding.UTF8.GetBytes(builder.ToString() + Environment.NewLine);
                await installLog.WriteAsync(bytes, cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Preview install command failed: {resolved.Command}\n{result.StdErr}".Trim());
            }
        }

        if (reported)
            await onProgress!(null);

        // The install steps above ran with their own augmented PATH, but the
        // agents (Cmd nodes and AI CLI adapters) launch later with the inherited
        // host-process environment. Surface the npm global bin directory there so
        // tools an install step put on disk are actually resolvable to those nodes.
        EnsureInstalledToolsOnProcessPath();
    }

    public async Task<ManagedPreviewProcess> LaunchServiceAsync(
        PreviewServiceConfig service, PreviewRuntime runtime, NetworkMode mode, CancellationToken cancellationToken)
    {
        // The service holds the runtime's feed credentials only while it runs: its
        // exit, however it comes, lets go, so a preview with nothing running keeps
        // no credential file.
        var feeds = runtime.Feeds.Acquire();
        StreamWriter? writer = null;
        SemaphoreSlim? writeGate = null;

        try
        {
            var resolved = await BuildResolvedStepAsync(service, runtime, service.Port, feeds, mode, cancellationToken);
            var logPath = Path.Combine(runtime.LogDirectory, $"{service.Name}.log");
            writer = new StreamWriter(AppendLog.Open(runtime.LogDirectory, logPath))
            {
                AutoFlush = true,
            };
            writeGate = new SemaphoreSlim(1, 1);

            // Echo the command before spawning, not after: everything from Process.Start
            // to the return must be incapable of throwing, or a started process would be
            // lost before its caller could ever track it (see StartAsync). It also puts
            // the command line ahead of the output it produced, and keeps this write off
            // the gate the pumps contend for.
            await writer.WriteLineAsync($"> {resolved.Command}");

            var process = await _host.StartCommandAsync(PreparedCommand(resolved), cancellationToken);

            // Subscribed before the check, so an exit that has already happened and
            // one still to come both let go (the lease ignores the second).
            _ = process.WaitForExitAsync().ContinueWith(_ => feeds.Dispose(), TaskScheduler.Default);
            if (process.HasExited)
                feeds.Dispose();

            // A service that dies on its own (a crash, the run pod ending) changes the
            // preview's state with nothing else to announce it.
            _ = process.WaitForExitAsync().ContinueWith(_ => _notifier.PublishAsync(runtime.WorkItemId), TaskScheduler.Default);

            var stdoutTask = PumpStreamAsync(process.StandardOutput, writer, writeGate, cancellationToken);
            var stderrTask = PumpStreamAsync(process.StandardError, writer, writeGate, cancellationToken);

            return new ManagedPreviewProcess(service, process, writer, writeGate, stdoutTask, stderrTask, logPath, feeds);
        }
        catch
        {
            // Only reachable while the spawn itself fails, so there is no process to
            // stop — just the log handle and feed lease this method took, which nothing
            // else will ever hold a reference to. StopProcessAsync owns them once the
            // returned ManagedPreviewProcess exists.
            writer?.Dispose();
            writeGate?.Dispose();
            feeds.Dispose();
            throw;
        }
    }

    public async Task StopAsync(ManagedPreviewProcess process, CancellationToken cancellationToken)
    {
        // Claim it first: from here on the Process gets killed and disposed, so an
        // unsynchronized reader (the preview proxy) must stop treating it as live.
        process.MarkStopped();

        try
        {
            if (!process.Process.HasExited)
            {
                if (!process.Process.TryKill(out var failure))
                {
                    _logger.LogWarning("Failed to kill preview service {Service}: {Failure}", process.Service.Name, failure);
                }
                else
                {
                    // Kill only signals; bound the wait so a process that never
                    // exits cannot hold the preview gate (or a teardown that has
                    // no cancellation) forever.
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    grace.CancelAfter(_timings.KillGrace);
                    try
                    {
                        await process.Process.WaitForExitAsync(grace.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Preview service {Service} was still alive {Seconds:0.###}s after being killed", process.Service.Name, _timings.KillGrace.TotalSeconds);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop preview service {Service}", process.Service.Name);
        }

        try
        {
            // The pumps end when the output pipes close. A process that survived the
            // kill keeps them open, so this wait carries the same bound.
            await Task.WhenAll(process.StdOutPump, process.StdErrPump).WaitAsync(_timings.KillGrace);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Preview service {Service} output was still open {Seconds:0.###}s after the stop; not waiting for it", process.Service.Name, _timings.KillGrace.TotalSeconds);
        }
        catch
        {
            // Ignore log pump failures during shutdown.
        }

        // Its exit already let go of the feed credentials unless the event has yet to
        // be delivered; a stop does not wait for that.
        process.FeedLease.Dispose();

        try
        {
            // Disposing the writer flushes whatever the pumps last buffered, so this is
            // real I/O and can fail. The process is already dead by here and there is
            // nothing a caller could do about a log handle, while letting it throw would
            // abandon every service StopRuntimeAsync has not reached yet.
            process.Writer.Dispose();
            process.WriteGate.Dispose();
            process.Process.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release preview service {Service} resources", process.Service.Name);
        }
    }

    private static async Task PumpStreamAsync(StreamReader reader, StreamWriter writer, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                await writeGate.WaitAsync(cancellationToken);
                try
                {
                    await writer.WriteLineAsync(line);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during teardown.
        }
        catch (ObjectDisposedException)
        {
            // Ignore disposal races during teardown.
        }
    }

    internal async Task<ResolvedStep> BuildResolvedStepAsync(
        PreviewCommandConfig step, PreviewRuntime runtime, string? currentPortAlias,
        PackageFeedFileShare.Lease feeds, NetworkMode mode, CancellationToken cancellationToken)
    {
        var workingDirectory = await ResolveWorkingDirectoryAsync(step.Cwd, runtime, currentPortAlias, cancellationToken);
        var environment = MergePreviewEnvironment(
            BuildDefaultEnvironment(runtime.StateDirectory),
            step.Env,
            value => PreviewTemplates.Resolve(value, runtime, currentPortAlias),
            runtime.CustomEnv,
            feeds.Environment);

        return new ResolvedStep(
            PreviewTemplates.Resolve(step.Command, runtime, currentPortAlias),
            workingDirectory,
            environment,
            mode,
            feeds.Path is null ? [] : [feeds.Path],
            RunInFor(runtime.WorktreePath));
    }

    /// <summary>
    /// The run or chat a preview of <paramref name="worktreePath"/> runs in. When runs are isolated a worktree
    /// that is in neither (one left on ILD's own disk from before the host was switched) is refused: its steps
    /// would otherwise start on ILD's own machine.
    /// </summary>
    private Guid? RunInFor(string worktreePath)
        => _host.HolderOf(worktreePath)
            ?? (_host.Kind == RunHostKind.Local
                ? null
                : throw new InvalidOperationException(
                    $"Refusing to preview {worktreePath}: runs are isolated and this worktree is not in a run, so its commands would run on ILD's own machine. Start a new run of the work item."));

    /// <summary>
    /// Layers a step's environment onto <paramref name="defaults"/>, which it returns.
    /// Precedence: base defaults &lt; per-service ild.config env (templated) &lt; repo
    /// custom .env (verbatim) &lt; package feed credentials.
    /// The committed config is the profile's default; the repo's .env is what the
    /// human who owns the repository typed for this deployment, so it wins — every
    /// key in it was set deliberately, and a value it cannot override is a value
    /// they cannot correct without editing the agent-writable worktree.
    /// The cost of that is worth knowing: a stale .env line shadows a computed
    /// ${PORT:...} value just as readily as an intended one (docs/configuration.md).
    /// </summary>
    internal static Dictionary<string, string> MergePreviewEnvironment(
        Dictionary<string, string> defaults,
        IReadOnlyDictionary<string, string>? configEnv,
        Func<string, string> resolveTemplate,
        IReadOnlyDictionary<string, string> customEnv,
        IReadOnlyDictionary<string, string> feedEnv)
    {
        if (configEnv != null)
        {
            foreach (var entry in configEnv)
            {
                defaults[entry.Key] = resolveTemplate(entry.Value);
            }
        }

        // Custom values are injected verbatim — they are user-authored secrets, not
        // ${PORT:...} templates, so they must not go through resolveTemplate: a
        // password containing ${...} would otherwise be rewritten, or rejected
        // outright as an unsupported token, for looking like something it isn't.
        foreach (var entry in customEnv)
        {
            defaults[entry.Key] = entry.Value;
        }

        // Last of all: the selected package feeds' credentials name a file only this
        // runtime can put in place, so nothing typed anywhere may repoint them.
        foreach (var entry in feedEnv)
        {
            defaults[entry.Key] = entry.Value;
        }

        return defaults;
    }

    /// <summary>
    /// The base environment every preview step starts from, before the service's own
    /// <c>env</c> and then the repository's <c>.env</c> are layered over it: only what
    /// belongs to the preview itself, the npm cache in its state directory.
    /// <para>
    /// Deliberately not <c>HOME</c>, <c>PATH</c> or the npm prefix. Those belong to
    /// wherever the step runs, which only the run host knows — a run container sets
    /// its own when it is made, and the local host gives a command ours
    /// (<see cref="LocalRunHost.Launch"/>). Built here from ILD's own, they
    /// named directories a run does not have, and replaced the ones it does.
    /// </para>
    /// </summary>
    internal static Dictionary<string, string> BuildDefaultEnvironment(string stateDirectory)
        => new(StringComparer.OrdinalIgnoreCase) { ["NPM_CONFIG_CACHE"] = PreviewPaths.NpmCacheOf(stateDirectory) };

    /// <summary>
    /// ILD's own <c>HOME</c>, the root of the npm global bin added to ILD's own PATH.
    /// </summary>
    private string ResolveHomeDirectory()
    {
        var home = _environment.Get("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            // Never a fixed path in world-writable /tmp: the step's npm would read
            // a planted ~/.npmrc. Same reasoning as the askpass helper.
            return AgentIsolation.CreatePrivateDirectory("home");
        }

        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>
    /// The directory where <c>npm install -g</c> places executables during a local
    /// install step (<see cref="LocalRunHost.Launch"/> points
    /// <c>NPM_CONFIG_PREFIX</c> at <c>$HOME/.local</c>, so global CLIs such as
    /// <c>vp</c> land in <c>$HOME/.local/bin</c>).
    /// </summary>
    private string GetNpmGlobalBinDirectory()
        => Path.Combine(ResolveHomeDirectory(), ".local", "bin");

    /// <summary>
    /// Add the npm global bin directory to the host process PATH so the agents can
    /// resolve tools an install step put there. Cmd nodes and the AI CLI adapters
    /// launch their processes with the inherited host-process environment, which
    /// does not otherwise include <c>$HOME/.local/bin</c>; without this,
    /// <c>npm install -g</c>'d binaries are invisible to every node that runs after
    /// the Start node's install. Idempotent.
    ///
    /// <para>
    /// It is <em>appended</em>, not prepended. This is the orchestrator's own PATH,
    /// and the directory being added is agent-writable, so a planted
    /// binary sharing a name with something the image ships would otherwise be
    /// preferred by every orchestrator-side spawn that resolves by bare name — git
    /// and npm through <c>ProcessRunner</c>, for instance. Appending means the
    /// image's copy wins ties and only genuinely new tools are contributed, which is
    /// all this was ever for. (The preview's own children still get it first, where a tool it installed
    /// beating one in the image is the intended behaviour.)
    /// </para>
    /// </summary>
    private void EnsureInstalledToolsOnProcessPath()
    {
        var npmBin = GetNpmGlobalBinDirectory();

        // Create the directory up front. It is only populated lazily (by an
        // `npm install -g` in a Start node), but agents launched with this PATH
        // — e.g. Claude Code — warn when a PATH entry does not exist on disk.
        Directory.CreateDirectory(npmBin);

        var currentPath = _environment.Get("PATH") ?? string.Empty;

        var alreadyPresent = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, npmBin, StringComparison.Ordinal));
        if (alreadyPresent)
            return;

        _environment.Set(
            "PATH",
            string.IsNullOrWhiteSpace(currentPath) ? npmBin : $"{currentPath}{Path.PathSeparator}{npmBin}");
    }


    private async Task<string> ResolveWorkingDirectoryAsync(string? cwd, PreviewRuntime runtime, string? currentPortAlias, CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(cwd)
            ? runtime.WorktreePath
            : PreviewTemplates.Resolve(cwd, runtime, currentPortAlias);

        var full = Path.GetFullPath(Path.IsPathRooted(resolved)
            ? resolved
            : Path.Combine(runtime.WorktreePath, resolved));

        // A volume may be shared by every work item of the repository, and the agent
        // can write it, so the directory is made without following what it left there.
        if (PreviewVolumes.Contain(runtime.Volumes, full))
        {
            try
            {
                await _host.CreateDirectoryAsync(full, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Preview cwd '{resolved}' could not be created: {ex.Message}", ex);
            }

            return full;
        }

        if (!full.StartsWith(runtime.WorktreePath, StringComparison.Ordinal)
            && !full.StartsWith(runtime.StateDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Preview cwd '{resolved}' escapes the worktree/state directory boundary.");
        }

        await _host.CreateDirectoryAsync(full, cancellationToken);
        return full;
    }

    /// <summary>
    /// The one place a preview child's process is
    /// built — both spawn sites (install steps and long-running services) go
    /// through it, so neither can drift from the other. Its environment is not
    /// built here: <see cref="PreparedCommand"/> hands the resolved step's to the host once, which merges it.
    ///
    /// <para>
    /// The command text comes from the worktree's <c>ild.config.json</c>, a file the
    /// agent writes and can trigger itself through the ILD MCP tools. It is
    /// therefore agent-authored code, and gets what an agent launch gets: none of the
    /// orchestrator's secrets, and the egress proxy of its network mode (ADR-0016).
    /// </para>
    ///
    /// <para>
    /// The environment is constructed, not inherited: .NET pre-populates a child's
    /// from the current process, which would otherwise hand a preview command the
    /// DB connection strings, the encryption-at-rest key, the orchestrator's own API
    /// tokens. Stripping happens before the resolved step's environment is
    /// applied, never after, so what it removes is only ever what was
    /// <em>inherited</em>: refusing to inherit is not refusing to be configured. A
    /// preview that legitimately needs one of those names sets it in
    /// <c>ild.config.json</c> or the repository's preview <c>.env</c>, and that
    /// value survives — pointed wherever the config points it rather than at the
    /// orchestrator's.
    /// </para>
    /// </summary>
    internal ProcessStartInfo BuildPreviewProcess(ResolvedStep resolved)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolved.WorkingDirectory,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(resolved.Command);

        return psi;
    }

    // What the caller sets on the process is passed on as such, and the credential file with it, so a host that starts the
    // command in a run of its own keeps the variables and copies the file in. It runs in the worktree's run, or nowhere.
    internal RunCommand PreparedCommand(ResolvedStep resolved)
        => RunCommand.Prepared(BuildPreviewProcess(resolved), environment: CommandEnvironment.Of(resolved.Environment), files: resolved.Files, networkMode: resolved.NetworkMode)
            with { RunIn = resolved.RunIn, EgressProxy = ProxyFor(resolved) };

    // The proxy a preview command is pointed at, signed for the run it runs in: none in network mode Off, or where no proxy runs.
    private string? ProxyFor(ResolvedStep resolved)
        => resolved.NetworkMode == NetworkMode.Off || _credentials is null ? null : _egressProxy?.ClientUrl(_credentials, new LaunchScope(LaunchKind.Preview, resolved.RunIn));

    // The run id leads the name, which is how the run's files are found and what a host checks before it copies one in.
    internal string FeedFilePrefix(string worktreePath, string kind)
        => _host.RunOf(worktreePath) is { } run ? $"{run:N}-{kind}" : kind;

    private async Task<CommandResult> RunCommandAsync(ResolvedStep resolved, CancellationToken cancellationToken)
    {
        using var process = await _host.StartCommandAsync(PreparedCommand(resolved), cancellationToken);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var limit = _outputLimits.MaxOutputBytes;
        var stdoutTask = BoundedOutput.PumpAsync(process.StandardOutput, limit, text => { stdout.Append(text); return Task.CompletedTask; }, cancellationToken);
        var stderrTask = BoundedOutput.PumpAsync(process.StandardError, limit, text => { stderr.Append(text); return Task.CompletedTask; }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.TryKill(out var failure))
                _logger.LogWarning("Failed to kill cancelled preview command: {Failure}", failure);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    internal sealed record ResolvedStep(
        string Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment,
        NetworkMode NetworkMode = NetworkMode.Monitor, IReadOnlyList<string>? Files = null, Guid? RunIn = null);

    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);
}
