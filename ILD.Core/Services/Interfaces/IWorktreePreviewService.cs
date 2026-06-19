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

public interface IWorktreePreviewService
{
    Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default);

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
    public Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public bool IsPreviewRunning(string worktreePath) => false;
}