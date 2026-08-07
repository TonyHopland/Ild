using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <param name="CustomEnv">
/// Raw text of the repository's custom <c>.env</c> (see <c>Repository.PreviewEnv</c>),
/// or null. Parsed at injection time and merged into every preview process's
/// environment last of all, so it beats both the base defaults and the per-service
/// <c>ild.config.json</c> env: every name in it was typed by the human who owns the
/// repository, and one they cannot override is one they cannot correct without
/// editing the worktree the coding agent writes. The values are injected verbatim —
/// they are secrets, not <c>${PORT:...}</c> templates, so a <c>$</c> in them survives
/// intact. The corollary is that a stale line here silently shadows a computed
/// <c>${PORT:...}</c> value in a service's <c>env</c>.
/// <para>
/// Security model: this text is stored encrypted at rest (via <c>SecretProtector</c>)
/// and is injected only as <em>process environment variables</em> on the preview
/// service/install <see cref="System.Diagnostics.ProcessStartInfo"/> — it is never
/// written to a file in the worktree, so it cannot be staged by a run's
/// <c>git add -A</c> and pushed into a PR. It also never enters the coding-agent's
/// own process environment. The plaintext is readable over the API at exactly one
/// place — <c>GET api/v1/repositories/{id}/preview-env</c>, so the human who owns the
/// repository can edit what is already stored — and that endpoint refuses the agent
/// service token outright; no agent-facing payload or MCP tool carries it. This
/// env-var-only injection is deliberately preferred over
/// materialising a <c>.env</c> file, which would be both committable and directly
/// readable.
/// </para>
/// </param>
/// <param name="WorkItemId">
/// The work item whose worktree is being previewed, or null when the caller does
/// not know it. Recorded on the runtime purely so a running service's advertised
/// <c>publicUrl</c> can be built as <c>wi-{id}.{proxy base}</c> when a
/// <see cref="PreviewProxyBase"/> is configured — nothing else about the preview
/// depends on it. Without an id (or without a proxy base) the URL falls back to
/// the historical <c>http://{publicHost}:{port}</c> form.
/// </param>
public sealed record WorktreePreviewStartOptions(
    string? ProfileName = null,
    bool SkipInstall = false,
    string? PublicHost = null,
    IReadOnlyDictionary<string, int>? PortOverrides = null,
    string? CustomEnv = null,
    string? WorkItemId = null);

/// <summary>
/// Why <see cref="IWorktreePreviewService.ResolvePreviewTargetAsync"/> could not
/// hand back a port. Each value is a distinct thing a human did wrong or has yet to
/// do, and only the resolver is in a position to know which — hence an outcome
/// rather than a bare bool.
/// <para>
/// The distinction is for ILD's own log, not for the caller: the proxy answers every
/// one of these with the same 404, because the alternative told an unauthenticated
/// stranger which work items exist and what is running in them. The outcome decides
/// how loudly it is logged — a misconfiguration is worth a warning, "nobody pressed
/// Start" is not — and lets tests pin each path exactly.
/// </para>
/// </summary>
public enum PreviewTargetOutcome
{
    /// <summary>A live port was found; the request can be forwarded.</summary>
    Resolved,

    /// <summary>
    /// The hostname sits under the preview base but its label is not
    /// <c>wi-{workItemId}</c> or <c>wi-{workItemId}-{serviceName}</c>.
    /// </summary>
    NotAPreviewHost,

    /// <summary>The work item id in the hostname does not resolve to a work item.</summary>
    UnknownWorkItem,

    /// <summary>The work item exists but has no worktree to preview (no run has created one).</summary>
    NoWorktree,

    /// <summary>The worktree exists but no preview runtime is active for it.</summary>
    PreviewNotRunning,

    /// <summary>
    /// The preview is running but the addressed service is not — either the named
    /// service is stopped/exited, or (for the bare <c>wi-{id}</c> form) no service
    /// marked <c>"public": true</c> is up.
    /// </summary>
    ServiceNotRunning,

    /// <summary>
    /// The bare <c>wi-{id}</c> form was used while more than one <c>"public": true</c>
    /// service is running, so it names no single service. Distinct from
    /// <see cref="ServiceNotRunning"/> because nothing is down: the caller has to
    /// pick, and the message lists the names to pick from. Advertised URLs never
    /// land here — a profile with several public services has each of them
    /// advertised as <c>wi-{id}-{name}</c>.
    /// </summary>
    AmbiguousService,
}

/// <summary>
/// Where a preview hostname points. On <see cref="PreviewTargetOutcome.Resolved"/>
/// the loopback <see cref="Port"/> and the service's <see cref="RewriteHost"/>
/// preference are set; otherwise <see cref="Message"/> explains what was missing.
/// That message is for ILD's log and names internal state freely — it is deliberately
/// never sent to the client.
/// </summary>
public sealed record PreviewTarget(
    PreviewTargetOutcome Outcome,
    int Port,
    string? ServiceName,
    bool RewriteHost,
    string Message)
{
    public bool IsResolved => Outcome == PreviewTargetOutcome.Resolved;

    public static PreviewTarget Resolved(int port, string serviceName, bool rewriteHost)
        => new(PreviewTargetOutcome.Resolved, port, serviceName, rewriteHost, $"Preview service '{serviceName}' is listening on port {port}.");

    public static PreviewTarget Failed(PreviewTargetOutcome outcome, string message)
        => new(outcome, 0, null, true, message);
}

/// <summary>
/// Result of <see cref="IWorktreePreviewService.InstallAsync"/>.
/// <see cref="Installed"/> is true when install steps actually ran; false when
/// the worktree has no <c>ild.config.json</c> preview profile to install — a
/// best-effort no-op, not a failure. <see cref="Message"/> carries the skip
/// reason so callers can surface it as a warning.
/// </summary>
public sealed record WorktreeInstallResult(bool Installed, string? Message = null);

/// <summary>
/// Result of <see cref="IWorktreePreviewService.ValidateConfigAsync"/>.
/// <see cref="Configured"/> is false when the worktree ships no
/// <c>ild.config.json</c> preview profile (a best-effort no-op, with the reason
/// in <see cref="Message"/>). When configured, <see cref="ProfileName"/> is the
/// resolved profile and <see cref="Services"/> lists its service names. The call
/// throws <see cref="InvalidOperationException"/> when a config is present but
/// invalid, so the precise reason can be surfaced to the author.
/// </summary>
public sealed record WorktreePreviewValidationResult(
    bool Configured,
    string? ProfileName,
    IReadOnlyList<string> Services,
    string? Message = null);

public interface IWorktreePreviewService
{
    Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a single preview service by its configured name, leaving any other
    /// running services untouched. The first service started for a worktree creates
    /// the shared runtime (allocating every profile service's port up front so
    /// cross-service <c>${PORT:&lt;alias&gt;}</c> references resolve, and running the
    /// profile's install steps unless <see cref="WorktreePreviewStartOptions.SkipInstall"/>
    /// is set); later calls reuse it. A service that is already running is returned
    /// as-is. Throws <see cref="InvalidOperationException"/> when the worktree has no
    /// preview config or the name does not resolve to a service.
    /// </summary>
    Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a single running preview service by name, leaving the others running.
    /// Stopping the last running service tears down the shared runtime. A name that
    /// is not currently running is a no-op that returns the current status.
    /// </summary>
    Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one service's entry in the worktree's <c>ild.config.json</c> as the
    /// raw (pretty-printed) JSON of that service object, so the Preview tab can edit
    /// it in place. <paramref name="profileName"/> defaults to the config's default
    /// profile. Returns null when the worktree has no preview config or the name does
    /// not resolve to a service.
    /// </summary>
    Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one service's entry in the worktree's <c>ild.config.json</c> with the
    /// supplied JSON, persisting the change to disk. The JSON is parsed and validated
    /// with the same per-service rules as the preview-start path (name/command/port
    /// alias/healthUrl/positive suggestedPort), and its <c>name</c> must match
    /// <paramref name="serviceName"/> — this editor updates a service in place, it does
    /// not rename or add one. Throws <see cref="InvalidOperationException"/> when the
    /// config is missing, the JSON is invalid, validation fails, or the service is not
    /// found. The change takes effect the next time the service is started.
    /// </summary>
    Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the tail of a preview service's captured stdout/stderr log so a human
    /// can see what a service printed — especially the failure output of a service
    /// that exited. <paramref name="serviceName"/> identifies the service by its
    /// configured name; the log lives in the worktree's preview state directory and
    /// persists across stop/start, so it is readable whether the service is running,
    /// exited, or fully stopped. Returns null when no log exists yet (the preview was
    /// never started) or the name does not resolve to a log file. Only the last
    /// <c>maxBytes</c> bytes are returned so a long-running service's log can't blow
    /// up the response.
    /// </summary>
    Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the install steps of an <c>ild.config.json</c> preview profile in the
    /// given worktree without starting any services. <paramref name="profileName"/>
    /// defaults to the config's default profile when null. When the worktree has no
    /// <c>ild.config.json</c> preview profile the install is skipped best-effort and
    /// the returned result reports <see cref="WorktreeInstallResult.Installed"/> as
    /// false — most projects ship no such file, so a missing config is not a failure.
    /// Throws only when a requested profile is missing or an install step exits
    /// non-zero. Used by the Start node to provision a worktree on run start.
    /// <paramref name="customEnv"/> is the repository's custom <c>.env</c> text
    /// (see <c>Repository.PreviewEnv</c>); when set it is parsed and injected into
    /// each install step's environment with the same precedence the service-start
    /// path gives it — last, over the step's own <c>env</c> — so install scripts see
    /// the same secrets, and the same overrides, the services will.
    /// </summary>
    Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, string? customEnv = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads and validates the worktree's <c>ild.config.json</c> preview config
    /// without installing, starting, or otherwise touching the worktree. Use it as
    /// a dry run after authoring or editing the file: it parses the config exactly
    /// as the preview-start path does and applies the same per-service validation
    /// (unique names, required command/port/healthUrl, positive suggestedPort).
    /// Returns <see cref="WorktreePreviewValidationResult.Configured"/> false when
    /// no preview profile is present, and throws <see cref="InvalidOperationException"/>
    /// with the precise reason when a config is present but invalid.
    /// </summary>
    Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight O(1) check whether a preview runtime is active for the given worktree path.
    /// Does not load config files — only inspects the in-memory runtime dictionary.
    /// </summary>
    bool IsPreviewRunning(string worktreePath);

    /// <summary>
    /// Turns a preview hostname's leading label into the loopback port a request
    /// should be forwarded to. <paramref name="hostLabel"/> is what
    /// <see cref="PreviewProxyBase.TryGetHostLabel"/> peeled off the <c>Host</c>
    /// header: <c>wi-{workItemId}</c> addresses the profile's single
    /// <c>"public": true</c> service, and <c>wi-{workItemId}-{serviceName}</c>
    /// addresses one service by name (service names may contain hyphens — the work
    /// item id is the digits immediately after <c>wi-</c>, so the split is
    /// unambiguous).
    /// <para>
    /// The whole chain lives here — label to work item to worktree to runtime to
    /// port — because every step has its own way of coming up empty and the caller
    /// needs to tell a human which one did. The work item lookup is passed in
    /// rather than injected: this service is a singleton and
    /// <see cref="IWorkItemManager"/> is request-scoped, so the caller supplies the
    /// one from its own scope.
    /// </para>
    /// </summary>
    Task<PreviewTarget> ResolvePreviewTargetAsync(string hostLabel, IWorkItemManager workItems, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op implementation of <see cref="IWorktreePreviewService"/> for environments
/// where preview is unavailable (e.g. unit tests without DI).
/// </summary>
public sealed class NoopPreviewService : IWorktreePreviewService
{
    public Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, string? customEnv = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public bool IsPreviewRunning(string worktreePath) => false;
    public Task<PreviewTarget> ResolvePreviewTargetAsync(string hostLabel, IWorkItemManager workItems, CancellationToken cancellationToken = default)
        => Task.FromResult(PreviewTarget.Failed(
            PreviewTargetOutcome.PreviewNotRunning,
            "Worktree previews are not available in this environment."));
}