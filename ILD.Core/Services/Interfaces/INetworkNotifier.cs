using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Sink for egress-filter notifications (in production: SignalR). Implementations
/// must be safe to call from any thread and never throw.
/// </summary>
public interface INetworkNotifier
{
    /// <summary>The mode or either list changed; listeners re-read the lists.</summary>
    Task PolicyChangedAsync();

    Task LogEntryAppendedAsync(NetworkLogEntry entry);

    Task LogClearedAsync();
}

public sealed class NoopNetworkNotifier : INetworkNotifier
{
    public Task PolicyChangedAsync() => Task.CompletedTask;
    public Task LogEntryAppendedAsync(NetworkLogEntry entry) => Task.CompletedTask;
    public Task LogClearedAsync() => Task.CompletedTask;
}
