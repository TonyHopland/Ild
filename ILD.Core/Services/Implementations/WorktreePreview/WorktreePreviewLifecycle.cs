using ILD.Core.Services.Interfaces;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// One worktree's preview, from the first start to the last stop: a state machine (idle, starting, running,
/// stopping) over the runtime that is running, the start in progress, and the teardown of both. It owns the one start
/// protocol — a whole profile and a single service are two scopes of it, not two protocols — and the places where the
/// two differ are declared in <see cref="PlanFor"/>.
/// <para>
/// A start registers a progress record before it waits for the context's gate, so a queued start already reads as
/// starting and a second start of no more than the same scope joins it. A start asking for more takes the record
/// over and queues at the gate; the start it overtook runs on, and a stop still ends both. The record is removed
/// when the start succeeds, finds nothing to do or is cancelled; a failed whole-profile start leaves it until the
/// worktree's next start or stop, since it is all there is left to report once everything the start launched is down.
/// </para>
/// </summary>
internal sealed class WorktreePreviewLifecycle : IDisposable
{
    private readonly PreviewLifecycleContext _ctx;
    private readonly ConcurrentDictionary<Task, byte> _ownedStarts = new();
    private readonly ConcurrentDictionary<StartProgress, byte> _overtaken = new();
    private volatile PreviewRuntime? _runtime;
    private StartProgress? _start;

    public WorktreePreviewLifecycle(string worktreePath, PreviewLifecycleContext context)
    {
        WorktreePath = worktreePath;
        _ctx = context;
    }

    public string WorktreePath { get; }

    /// <summary>The services this worktree's preview has launched, null until a start has succeeded (or a single service has launched).</summary>
    public PreviewRuntime? Runtime => _runtime;

    /// <summary>Whether a start is still bringing the preview up.</summary>
    public bool IsStarting => Volatile.Read(ref _start) is { InProgress: true };

    /// <summary>How many starts wait on the start in progress; lets a test order a stop after a join.</summary>
    public int JoinedStartCount => Volatile.Read(ref _start)?.Joined ?? 0;

    /// <summary>How many starts still run after a wider start took the record over; lets a test order a stop after that.</summary>
    public int OvertakenStartCount => _overtaken.Count;

    /// <summary>
    /// Whether the start now on record brings up the whole preview. An overtaken start is listed before the wider
    /// one is registered, so a test that means to stop after the takeover waits for this as well as the count.
    /// </summary>
    public bool IsStartingWhole => Volatile.Read(ref _start) is { InProgress: true } start && start.Scope.IsWhole;

    /// <summary>The runtime when it is leaving the lifecycle, so the caller can retire it.</summary>
    public PreviewRuntime? TakeRuntime() => Interlocked.Exchange(ref _runtime, null);

    public WorktreePreviewResponse Status(LoadedPreviewConfig loaded)
    {
        var runtime = _runtime;
        if (runtime != null && runtime.Processes.Any(p => p.IsRunning))
            return _ctx.Responses.BuildResponse(loaded, runtime);

        if (Volatile.Read(ref _start) is { } start)
            return start.Snapshot;

        return runtime != null ? _ctx.Responses.BuildResponse(loaded, runtime) : _ctx.Responses.BuildStoppedResponse(loaded);
    }

    public string State()
    {
        if (IsStarting)
            return "starting";
        if (_runtime is not { } runtime)
            return "stopped";

        var processes = runtime.Processes;
        var running = processes.Count(p => p.IsRunning);
        if (processes.Count > running)
            return "failed";
        if (running == 0)
            return "stopped";
        return running >= runtime.ServiceCount ? "running" : "partial";
    }

    /// <summary>
    /// Brings up <paramref name="scope"/> of the worktree's preview and answers with how it ended. A start already
    /// bringing up that much is joined instead of run a second time, and answers as its owner does.
    /// </summary>
    public async Task<WorktreePreviewResponse> StartAsync(
        LoadedPreviewConfig loaded, PreviewStartScope scope, WorktreePreviewStartOptions? options, CancellationToken cancellationToken)
    {
        var (pending, running) = await PrepareAsync(loaded, scope, options ?? new WorktreePreviewStartOptions(), cancellationToken);
        if (running != null)
            return running;

        if (pending!.Owned)
        {
            var run = RunStartAsync(pending, cancellationToken);
            Track(run);
            return await run;
        }

        pending.Progress.Join();
        return await pending.Progress.Outcome.WaitAsync(cancellationToken);
    }

    /// <summary>As <see cref="StartAsync"/>, but answers as soon as the start is registered and lets it run on; how it ends is what the status reports.</summary>
    public async Task<WorktreePreviewResponse> BeginAsync(
        LoadedPreviewConfig loaded, PreviewStartScope scope, WorktreePreviewStartOptions? options, CancellationToken cancellationToken)
    {
        var (pending, running) = await PrepareAsync(loaded, scope, options ?? new WorktreePreviewStartOptions(), cancellationToken);
        if (running != null)
            return running;
        if (!pending!.Owned)
            return Status(loaded);

        Track(Task.Run(() => RunBackgroundStartAsync(pending), CancellationToken.None));
        return pending.Progress.Snapshot;
    }

    public async Task<WorktreePreviewResponse> StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _start) is { InProgress: true } starting)
            starting.Stop();
        foreach (var overtaken in _overtaken.Keys)
            overtaken.Stop();

        await _ctx.Gate.WaitAsync(cancellationToken);
        try
        {
            DropFinishedStart();
            var loaded = await _ctx.Config.LoadAsync(WorktreePath, cancellationToken);
            if (TakeRuntime() is { } runtime)
            {
                await _ctx.RetireRuntimeAsync(runtime, cancellationToken);
                return _ctx.Responses.BuildStoppedResponse(loaded, runtime.ProfileName, runtime.PublicHost, runtime.StateDirectory, loaded.ConfigPath);
            }

            await EndLeftBehindAsync(cancellationToken);
            return _ctx.Responses.BuildStoppedResponse(loaded);
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    public async Task<WorktreePreviewResponse> StopServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        await _ctx.Gate.WaitAsync(cancellationToken);
        try
        {
            DropFinishedStart();
            var loaded = await _ctx.Config.LoadAsync(WorktreePath, cancellationToken);
            if (_runtime is not { } runtime)
                return _ctx.Responses.BuildStoppedResponse(loaded);

            var process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            if (process != null)
            {
                await _ctx.Launcher.StopAsync(process, cancellationToken);
                runtime.RemoveProcess(process);
            }

            if (runtime.Processes.Count == 0)
            {
                TakeRuntime();
                await _ctx.RetireRuntimeAsync(runtime, cancellationToken);
                return _ctx.Responses.BuildStoppedResponse(loaded, runtime.ProfileName, runtime.PublicHost, runtime.StateDirectory, loaded.ConfigPath);
            }

            return _ctx.Responses.BuildResponse(loaded, runtime);
        }
        finally
        {
            _ctx.Gate.Release();
        }
    }

    /// <summary>
    /// Waits for every start this lifecycle runs to tear down what it launched (the context has been told to shut
    /// down, which they observe), then retires the runtime. How a start ended is its caller's to observe, so a start
    /// that ends in an exception is only waited for.
    /// </summary>
    public void Dispose()
    {
        Task.WhenAll(_ownedStarts.Keys).ContinueWith(all => _ = all.Exception, TaskScheduler.Default)
            .Wait(TimeSpan.FromSeconds(30));

        if (TakeRuntime() is not { } runtime)
            return;

        try
        {
            _ctx.RetireRuntimeAsync(runtime, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort on shutdown.
        }
    }

    private void Track(Task run)
    {
        _ownedStarts[run] = 0;
        _ = run.ContinueWith(done => _ownedStarts.TryRemove(done, out _), TaskScheduler.Default);
    }

    private async Task RunBackgroundStartAsync(PendingStart pending)
    {
        try
        {
            await RunStartAsync(pending, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The service is being disposed; the start has already torn down what it launched.
        }
        catch (PreviewStartStoppedException)
        {
            // Stopped on purpose; the stop has the start's teardown to report.
        }
        catch (Exception ex)
        {
            // The failure is what get_preview reports; this is for the operator's log.
            _ctx.Logger.LogWarning(ex, "Background start of the preview in {Worktree} failed", WorktreePath);
        }
    }

    private async Task<WorktreePreviewResponse> RunStartAsync(PendingStart pending, CancellationToken cancellationToken)
    {
        try
        {
            var response = await StartCoreAsync(pending, cancellationToken);
            pending.Progress.Succeeded(response);
            return response;
        }
        catch (Exception ex)
        {
            pending.Progress.Failed(ex);
            throw;
        }
        finally
        {
            _overtaken.TryRemove(pending.Progress, out _);
        }
    }

    /// <summary>
    /// Answers a preview that is already running as-is, and otherwise selects and validates the profile and
    /// registers a progress record for the start — before it waits for the gate, so a queued start already reads as
    /// starting. When a start already in progress brings up at least what this one asks for, this one is handed that
    /// start's record to follow instead of running a second start. Otherwise it registers its own over it without
    /// waiting, so a stop reaches it and a background start answers at once; the gate is what orders the two.
    /// </summary>
    private async Task<(PendingStart? Start, WorktreePreviewResponse? Running)> PrepareAsync(
        LoadedPreviewConfig loaded, PreviewStartScope scope, WorktreePreviewStartOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_ctx.IsDisposed, this);
        PreviewConfigLoader.RequireConfigured(loaded);

        // A start that replaces the runtime finds the answer here; a single service is only known to be up under the gate.
        var plan = PlanFor(scope);
        if (plan.ReplacesRuntime && RunningResponse(loaded) is { } running)
            return (null, running);

        var profile = PreviewConfigLoader.Resolve(loaded, options.ProfileName);
        if (!plan.ReplacesRuntime)
            FindService(profile, scope.ServiceName!);

        while (true)
        {
            var current = Volatile.Read(ref _start);
            var inFlight = current != null && Joinable(current);
            if (inFlight && current!.Scope.Covers(scope))
                return (new PendingStart(profile, scope, options, current, Owned: false), null);

            // Only a start that will own the record is refused up front: one that joins runs under its
            // owner's options. The claim inside the start still decides; this only answers sooner.
            if (plan.ReplacesRuntime && options.Volumes is { } volumes && _ctx.Claims.RefusalFor(volumes, WorktreePath) is { } refusal)
                throw new InvalidOperationException(refusal.Message);

            var mine = new StartProgress(
                _ctx.Responses.BuildPendingResponse(loaded, profile.Name, scope.ServiceName is { } name ? [name] : null),
                options.WorkItemId,
                scope);
            // Listed before the swap, so a stop never finds the new start without the one it overtook.
            if (inFlight)
                _overtaken[current!] = 0;
            var registered = Interlocked.CompareExchange(ref _start, mine, current) == current;
            // It may have ended since it was read, and then nothing is left to take it out again.
            if (inFlight && current!.Outcome.IsCompleted)
                _overtaken.TryRemove(current, out _);
            if (!registered)
                continue;

            await _ctx.Notifier.PublishAsync(mine.WorkItemId);
            return (new PendingStart(profile, scope, options, mine, Owned: true), null);
        }
    }

    /// <summary>A start still bringing the preview up; one already stopping will not, so it is not joined.</summary>
    private static bool Joinable(StartProgress start) => start.InProgress && !start.Stopping.IsCancellationRequested;

    private WorktreePreviewResponse? RunningResponse(LoadedPreviewConfig loaded)
    {
        if (_runtime is not { } existing)
            return null;

        var response = _ctx.Responses.BuildResponse(loaded, existing);
        return response.State == "running" ? response : null;
    }

    private static PreviewServiceConfig FindService(PreviewProfile profile, string serviceName)
        => profile.Services.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Preview service '{serviceName}' not found in profile '{profile.Name}'.");

    /// <summary>
    /// How a start differs by scope — every place the two flavours of the one protocol part ways:
    /// <list type="bullet">
    /// <item><description><see cref="StartPlan.ReplacesRuntime"/>: a whole profile starts on a fresh runtime, retiring the one it finds; a single service joins the running one.</description></item>
    /// <item><description><see cref="StartPlan.HealthTogether"/>: a whole profile launches every service and then waits for all of them at once, each failing on its own; a single service is waited for before the next launches, since the next may point at it, and the first failure ends the start.</description></item>
    /// <item><description><see cref="StartPlan.RegistersBeforeHealth"/>: a whole profile's runtime becomes reachable only once every service is healthy; a single service's is registered as it is created, so the service shows as running while it is waited for.</description></item>
    /// <item><description><see cref="StartPlan.TearsDownOnFailure"/>: a failed whole profile takes everything it launched down; a failed single service stays, so its log and exit code stay readable, and only a runtime left with nothing running is dropped.</description></item>
    /// </list>
    /// </summary>
    private static StartPlan PlanFor(PreviewStartScope scope)
        => scope.IsWhole
            ? new StartPlan(ReplacesRuntime: true, HealthTogether: true, RegistersBeforeHealth: false, TearsDownOnFailure: true)
            : new StartPlan(ReplacesRuntime: false, HealthTogether: false, RegistersBeforeHealth: true, TearsDownOnFailure: false);

    /// <summary>
    /// The services a single-service start launches, dependencies first. Past here the call really does launch the
    /// service, which is what makes the guard its business: a reference to a service that is not up cannot be
    /// honoured, and launching anyway is the failure it exists to prevent. It runs before anything is created, so a
    /// refusal leaves nothing behind. With StartDependencies the caller asked for the missing owners to be launched
    /// first instead of refused.
    /// </summary>
    private static IReadOnlyList<PreviewServiceConfig> ServicesToLaunch(PreviewStartScope scope, PreviewProfile profile, PreviewRuntime? runtime)
    {
        var service = FindService(profile, scope.ServiceName!);
        if (scope.StartDependencies)
            return PreviewDependencies.DependenciesToStart(service, profile.Name, profile.Config, runtime);

        PreviewDependencies.EnsureCrossReferencedServicesAreRunning(service, profile.Name, profile.Config, runtime);
        return [service];
    }

    private async Task<WorktreePreviewResponse> StartCoreAsync(PendingStart pending, CancellationToken callerToken)
    {
        var (profile, scope, options, progress) = (pending.Profile, pending.Scope, pending.Options, pending.Progress);
        var plan = PlanFor(scope);
        using var stoppable = CancellationTokenSource.CreateLinkedTokenSource(callerToken, progress.Stopping, _ctx.Shutdown.Token);
        var cancellationToken = stoppable.Token;

        PreviewDataClaims.Lease? lease = null;
        try
        {
            await _ctx.Gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await EndStartAsync(progress);
            ThrowIfStopped(progress, callerToken);
            throw;
        }

        PreviewRuntime? runtime;
        IReadOnlyList<PreviewServiceConfig> order;
        try
        {
            var loaded = profile.Loaded;
            if (plan.ReplacesRuntime)
            {
                if (RunningResponse(loaded) is { } running)
                {
                    await EndStartAsync(progress);
                    return running;
                }

                if (TakeRuntime() is { } existing)
                    await _ctx.RetireRuntimeAsync(existing, cancellationToken);
                else
                    await EndLeftBehindAsync(cancellationToken);

                runtime = null;
                order = profile.Services;
            }
            else
            {
                runtime = _runtime;
                var live = runtime?.Processes.FirstOrDefault(p =>
                    string.Equals(p.Service.Name, scope.ServiceName, StringComparison.OrdinalIgnoreCase) && !p.Process.HasExited);
                if (live != null)
                {
                    await EndStartAsync(progress);
                    return _ctx.Responses.BuildResponse(loaded, runtime!);
                }

                order = ServicesToLaunch(scope, profile, runtime);
                if (runtime == null)
                    await EndLeftBehindAsync(cancellationToken);

                await ReportAsync(progress, response =>
                {
                    foreach (var service in order)
                        response.Services.Single(row => row.Name == service.Name).Status = "pending";
                });
            }

            var mode = await _ctx.LaunchModeAsync(cancellationToken);
            if (runtime == null)
            {
                lease = await _ctx.ClaimPreviewDataAsync(options.Volumes, WorktreePath, holdsGate: true, cancellationToken);
                runtime = await CreateRuntimeAsync(profile, options, lease, mode, cancellationToken, progress);
                if (plan.RegistersBeforeHealth)
                {
                    _runtime = runtime;
                    lease = null;
                }
            }

            // A whole profile's runtime only becomes reachable — to StopAsync, to Dispose, to every
            // status surface — once the start has succeeded. Anything that fails before
            // then must therefore take the services it already launched down with
            // it: an orphan keeps running with this attempt's ports and ${PORT:alias}
            // values baked in, holds those ports against the next start, and keeps
            // appending to the same per-service log, with no stop path able to reach it.
            Exception? failure;
            try
            {
                failure = await LaunchAndAwaitHealthAsync(plan, order, profile, runtime, mode, options, progress, cancellationToken);
            }
            catch
            {
                await AbandonAsync(plan, runtime);
                throw;
            }

            if (failure != null)
            {
                await AbandonAsync(plan, runtime);
                ExceptionDispatchInfo.Throw(failure);
            }

            if (!plan.RegistersBeforeHealth)
            {
                _runtime = runtime;
                lease = null;
            }

            await EndStartAsync(progress);
            return _ctx.Responses.BuildResponse(loaded, runtime);
        }
        catch (OperationCanceledException)
        {
            await EndStartAsync(progress);
            ThrowIfStopped(progress, callerToken);
            throw;
        }
        catch (Exception ex)
        {
            if (plan.TearsDownOnFailure)
                await ReportAsync(progress, response => PreviewResponses.MarkFailed(response, ex));
            else
                await EndStartAsync(progress);
            throw;
        }
        finally
        {
            lease?.Dispose();
            _ctx.Gate.Release();
        }
    }

    /// <summary>What a failed start leaves behind, per <see cref="StartPlan.TearsDownOnFailure"/>.</summary>
    private async Task AbandonAsync(StartPlan plan, PreviewRuntime runtime)
    {
        if (plan.TearsDownOnFailure)
        {
            await TearDownFailedStartAsync(runtime);
            return;
        }

        // Nothing of this preview runs, and its state reads as stopped, so no stop
        // control would ever be offered for it: left registered, it would hold the
        // repository's preview data until a restart.
        if (runtime.Processes.Count == 0 && ReferenceEquals(Interlocked.CompareExchange(ref _runtime, null, runtime), runtime))
            await _ctx.RetireRuntimeAsync(runtime, CancellationToken.None);
    }

    private async Task TearDownFailedStartAsync(PreviewRuntime runtime)
    {
        try
        {
            // Teardown is not the caller's to cancel: the token that just aborted
            // the start is typically already cancelled, and stopping is what makes
            // the failure clean.
            await _ctx.StopRuntimeAsync(runtime, CancellationToken.None);
        }
        catch (Exception teardownEx)
        {
            // Why the start failed is what the caller has to act on; that the
            // cleanup after it also failed is a second fact about the same event,
            // never a replacement for the first.
            _ctx.Logger.LogError(teardownEx, "Failed to stop preview services after a failed start of profile {Profile} in {Worktree}", runtime.ProfileName, WorktreePath);
        }
    }

    /// <summary>
    /// Launches the plan's services in turn and waits for them to become healthy, as <see cref="PlanFor"/> says. With
    /// <see cref="StartPlan.HealthTogether"/> a service that fails does not stop the others and the first failure
    /// (launch order, then service order) is returned once every service has settled; otherwise the first failure
    /// ends the start. Cancellation throws.
    /// </summary>
    private async Task<Exception?> LaunchAndAwaitHealthAsync(
        StartPlan plan, IReadOnlyList<PreviewServiceConfig> order, PreviewProfile profile, PreviewRuntime runtime, NetworkMode mode,
        WorktreePreviewStartOptions options, StartProgress progress, CancellationToken cancellationToken)
    {
        var singlePublicService = PreviewResponses.HasSinglePublicService(profile.Services);
        var failures = new List<Exception?>();
        var launched = new List<(PreviewServiceConfig Service, ManagedPreviewProcess Process, string HealthUrl)>();
        foreach (var service in order)
        {
            try
            {
                // An exited process lingers in the runtime so its log/exit code
                // stays visible; restarting the service replaces it cleanly.
                if (runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, service.Name, StringComparison.OrdinalIgnoreCase)) is { } lingering)
                {
                    await _ctx.Launcher.StopAsync(lingering, cancellationToken);
                    runtime.RemoveProcess(lingering);
                }

                _ctx.Ports.EnsureServicePortAllocated(service, runtime.Ports, options.PortOverrides);

                var process = await _ctx.Launcher.LaunchServiceAsync(service, runtime, mode, cancellationToken);
                runtime.AddProcess(process);
                var healthUrl = PreviewTemplates.ResolveHealthUrl(service, runtime);
                var port = runtime.Ports.TryGetValue(service.Port, out var allocated) ? allocated : (int?)null;
                var publicUrl = service.Public ? _ctx.Responses.BuildPublicUrl(service, runtime, port, singlePublicService) : null;
                await ReportServiceAsync(progress, service.Name, row =>
                {
                    row.Status = "starting";
                    row.HealthUrl = healthUrl;
                    row.PublicUrl = publicUrl;
                    row.LocalUrl = PreviewUrls.ComposeLocalUrl(port);
                    row.LogFilePath = process.LogFilePath;
                    row.ProcessId = process.Process.Id;
                });
                launched.Add((service, process, healthUrl));

                if (!plan.HealthTogether)
                {
                    var unhealthy = await AwaitHealthAsync(service, process, healthUrl, runtime.WorktreePath, progress, cancellationToken);
                    if (unhealthy != null)
                        return unhealthy;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (plan.HealthTogether)
            {
                failures.Add(ex);
                await ReportServiceAsync(progress, service.Name, row =>
                {
                    row.Status = "failed";
                    row.Error = ex.Message;
                });
            }
        }

        if (plan.HealthTogether)
            failures.AddRange(await Task.WhenAll(launched.Select(l => AwaitHealthAsync(l.Service, l.Process, l.HealthUrl, runtime.WorktreePath, progress, cancellationToken))));
        return failures.FirstOrDefault(failure => failure != null);
    }

    private async Task<Exception?> AwaitHealthAsync(
        PreviewServiceConfig service, ManagedPreviewProcess process, string healthUrl, string worktreePath, StartProgress progress, CancellationToken cancellationToken)
    {
        await ReportServiceAsync(progress, service.Name, row => row.Status = "waitingForHealth");

        Exception? failure = null;
        try
        {
            await _ctx.Probe.WaitForHealthAsync(
                service.Name, healthUrl, worktreePath, service.HealthTimeoutSeconds ?? PreviewConfigLoader.DefaultHealthTimeoutSeconds, process, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }

        await ReportServiceAsync(progress, service.Name, row =>
        {
            if (failure == null)
            {
                row.Status = "running";
                return;
            }

            row.Status = "failed";
            row.Error = failure.Message;
            row.ExitCode = process.Process.HasExited ? process.Process.ExitCode : null;
        });
        return failure;
    }

    /// <summary>
    /// Builds the shared runtime for a worktree: resolves the public host, creates the
    /// state directory, allocates every profile service's port up front (so per-service
    /// starts resolve cross-service <c>${PORT:&lt;alias&gt;}</c> references), and runs the
    /// install steps unless skipped. Does not launch any service or store the runtime —
    /// the caller owns process startup. The ports and the install step reached are
    /// published to the <paramref name="progress"/> record as they become known.
    /// </summary>
    private async Task<PreviewRuntime> CreateRuntimeAsync(
        PreviewProfile profile,
        WorktreePreviewStartOptions options,
        PreviewDataClaims.Lease? lease,
        NetworkMode mode,
        CancellationToken cancellationToken,
        StartProgress progress)
    {
        var publicHost = _ctx.ResolvePublicHost(options.PublicHost);
        var stateDirectory = await _ctx.Launcher.BuildStateDirectoryAsync(WorktreePath, cancellationToken);
        var logDirectory = PreviewPaths.BuildLogDirectory(WorktreePath);

        var ports = _ctx.Ports.AllocatePorts(profile.Config, options.PortOverrides);
        var runtime = new PreviewRuntime(
            WorktreePath,
            profile.ConfigPath,
            profile.Name,
            stateDirectory,
            logDirectory,
            publicHost,
            ports,
            new List<ManagedPreviewProcess>(),
            DotEnvParser.Parse(options.CustomEnv),
            new PackageFeedFileShare(options.PackageFeeds ?? [], _ctx.Launcher.FeedFilePrefix(WorktreePath, "preview"), _ctx.Logger),
            await PreviewVolumes.CreateFoldersAsync(_ctx.Host, _ctx.Host, WorktreePath, profile.Config.Volumes, options.Volumes, stateDirectory, _ctx.Logger, cancellationToken),
            lease,
            options.WorkItemId)
        {
            ServiceCount = profile.Services.Count,
        };

        await ReportAsync(progress, response =>
        {
            response.PublicHost = publicHost;
            response.StateDirectory = stateDirectory;
            foreach (var service in profile.Services)
                response.Services.Single(row => row.Name == service.Name).Port = ports.TryGetValue(service.Port, out var port) ? port : null;
        });

        if (!options.SkipInstall)
        {
            await _ctx.Launcher.RunInstallStepsAsync(profile.Config.Install, runtime, mode, cancellationToken,
                install => ReportAsync(progress, response =>
                {
                    response.State = install == null ? "starting" : "installing";
                    response.Install = install;
                }));
        }

        return runtime;
    }

    /// <summary>
    /// Ends the preview processes an earlier ILD left running in the worktree's run or chat when it ended without
    /// stopping them: this ILD knows nothing of them, and they hold the ports the next start needs. Skipped while
    /// another worktree of the same chat has a preview this ILD started, since the two share what is ended.
    /// </summary>
    private async Task EndLeftBehindAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Host.HolderOf(WorktreePath) is not { } holder) return;
        if (_ctx.Lifecycles.Any(other => other.Runtime != null && _ctx.Host.HolderOf(other.WorktreePath) == holder)) return;
        try
        {
            if (await _ctx.Host.KillUnownedAsync(holder, cancellationToken) is > 0 and var ended)
                _ctx.Logger.LogInformation("Ended {Count} preview process(es) left running in {Worktree} by an ILD that did not stop them", ended, WorktreePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ctx.Logger.LogWarning(ex, "Could not end the preview processes left running in {Worktree}", WorktreePath);
        }
    }

    /// <summary>Takes the start's progress record off the worktree, when it is still the one registered.</summary>
    private async Task EndStartAsync(StartProgress progress)
    {
        if (Interlocked.CompareExchange(ref _start, null, progress) == progress)
            await _ctx.Notifier.PublishAsync(progress.WorkItemId);
    }

    private void DropFinishedStart()
    {
        if (Volatile.Read(ref _start) is { InProgress: false } start)
            Interlocked.CompareExchange(ref _start, null, start);
    }

    private static void ThrowIfStopped(StartProgress progress, CancellationToken callerToken)
    {
        if (progress.Stopping.IsCancellationRequested && !callerToken.IsCancellationRequested)
            throw new PreviewStartStoppedException();
    }

    private Task ReportServiceAsync(StartProgress progress, string serviceName, Action<WorktreePreviewServiceResponse> change)
        => ReportAsync(progress, response => change(response.Services.Single(row => row.Name == serviceName)));

    private async Task ReportAsync(StartProgress progress, Action<WorktreePreviewResponse> change)
    {
        progress.Update(change);
        await _ctx.Notifier.PublishAsync(progress.WorkItemId);
    }

    private sealed class PreviewStartStoppedException()
        : InvalidOperationException("The preview was stopped before it finished starting.");

    private sealed record PendingStart(PreviewProfile Profile, PreviewStartScope Scope, WorktreePreviewStartOptions Options, StartProgress Progress, bool Owned);

    private sealed record StartPlan(bool ReplacesRuntime, bool HealthTogether, bool RegistersBeforeHealth, bool TearsDownOnFailure);
}
