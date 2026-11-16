using System.Collections.Concurrent;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>What the engine does for a direct route (network mode Off): the route is the pod's or container's, not a process's.</summary>
public interface IDirectRouteEngine
{
    /// <summary>Give the run's pod or container a route to the network that does not go through ILD. Idempotent.</summary>
    Task OpenAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>Take the direct route away. Idempotent, and nothing when there is none.</summary>
    Task CloseAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>Whether the run's pod or container has the direct route now, as the engine or cluster says.</summary>
    Task<bool> HasAsync(ExecOwner owner, CancellationToken ct);

    /// <summary>The runs and the chat container that have a direct route now.</summary>
    Task<IReadOnlyList<DirectRoute>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Which processes keep a run's (or the chats') direct route open, and the opening and closing of it. The count of live processes
/// launched in mode Off is the only thing that keeps a route open: nothing in the container is trusted, since the run writes its
/// own process files. After a restart it is empty, so the next proxied launch closes the route.
/// </summary>
internal sealed class DirectRoutes(IDirectRouteEngine engine, Func<CancellationToken, Task> ensureChat)
{
    private readonly ConcurrentDictionary<ExecOwner, int> _liveOff = new();

    // Opening and closing a route are one at a time for a container or pod; every chat shares one.
    private readonly ConcurrentDictionary<ExecOwner, SemaphoreSlim> _routeGates = new();

    /// <summary>
    /// Counts a process of mode Off before the route is opened, so a proxied launch checking at the same moment sees it, then opens the
    /// route (the chats' is their container's, which has to be there to be given one). The count is the result's until it is disposed,
    /// which is when the process ends; a failure to open gives it back.
    /// </summary>
    public async Task<RouteHold> OpenForProcessAsync(ExecOwner owner, CancellationToken ct)
    {
        _liveOff.AddOrUpdate(owner, 1, (_, n) => n + 1);
        var hold = new RouteHold(() => End(owner));
        try
        {
            await OpenAsync(owner, ct);
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    /// <summary>Takes the route away unless a process of mode Off still needs it; a proxied launch is where that happens.</summary>
    public async Task CloseIfUnusedAsync(ExecOwner owner, CancellationToken ct)
    {
        using var gate = await GateAsync(owner, ct);
        if (!await engine.HasAsync(owner, ct)) return;
        // A chat container is every chat's, so one chat's Off process keeps it for all.
        if (_liveOff.Any(e => e.Value > 0 && (owner.Chat ? e.Key.Chat : e.Key == owner))) return;
        await engine.CloseAsync(owner, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the route gate, unless a chat command in mode Off has the chat container's route open (or one is
    /// about to be): then null, since a connection made then would be no escape to test.
    /// </summary>
    public async Task<T?> WhileChatRouteClosedAsync<T>(ExecOwner chat, Func<CancellationToken, Task<T?>> work, CancellationToken ct) where T : struct
    {
        using var gate = await GateAsync(chat, ct);
        if (_liveOff.Any(e => e.Value > 0 && e.Key.Chat) || await engine.HasAsync(chat, ct)) return null;
        return await work(ct);
    }

    public Task<IReadOnlyList<DirectRoute>> ListAsync(CancellationToken ct) => engine.ListAsync(ct);

    private async Task OpenAsync(ExecOwner owner, CancellationToken ct)
    {
        using var gate = await GateAsync(owner, ct);
        if (owner.Chat) await ensureChat(ct);
        await engine.OpenAsync(owner, ct);
    }

    private void End(ExecOwner owner) => _liveOff.AddOrUpdate(owner, 0, (_, n) => Math.Max(0, n - 1));

    private async Task<IDisposable> GateAsync(ExecOwner owner, CancellationToken ct)
    {
        var gate = _routeGates.GetOrAdd(owner.Chat ? new ExecOwner(Guid.Empty, true) : owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>The count a process of mode Off holds on its owner's direct route. Disposing it more than once gives it back once.</summary>
internal sealed class RouteHold(Action end) : IDisposable
{
    private Action? _end = end;

    public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
}
