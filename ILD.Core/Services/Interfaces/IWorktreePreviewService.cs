using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

public sealed record WorktreePreviewStartOptions(
    string? ProfileName = null,
    bool SkipInstall = false,
    string? PublicHost = null,
    IReadOnlyDictionary<string, int>? PortOverrides = null);

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
    /// </summary>
    Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default);

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
    public Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public bool IsPreviewRunning(string worktreePath) => false;
}