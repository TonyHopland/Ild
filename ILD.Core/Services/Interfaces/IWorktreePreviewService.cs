using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

public sealed record WorktreePreviewStartOptions(
    string? ProfileName = null,
    bool SkipInstall = false,
    string? PublicHost = null,
    IReadOnlyDictionary<string, int>? PortOverrides = null);

public interface IWorktreePreviewService
{
    Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default);
    Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default);

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
    public bool IsPreviewRunning(string worktreePath) => false;
}