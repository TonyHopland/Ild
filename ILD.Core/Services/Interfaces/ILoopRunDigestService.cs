using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Reads one loop run as a <see cref="LoopRunDigest"/>, from stored data only: the
/// pull request comes from the run's last PR snapshot, never from the forge.
/// </summary>
public interface ILoopRunDigestService
{
    /// <summary>The run's digest, or null when no such run exists.</summary>
    Task<LoopRunDigest?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
}
