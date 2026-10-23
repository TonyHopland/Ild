namespace ILD.Core.Services.Remote;

/// <summary>
/// Lets one claimer of Ready work items in at a time within this process: a
/// scheduler pass or a hand start (<see cref="IRemoteWorkItemCoordinator.StartIgnoringRepositoryLimitAsync"/>).
/// Each counts the slots in use before it claims, so letting them overlap
/// would let both claim against the same free slot. Registered as a singleton,
/// because the scheduler and a request resolve their coordinators in scopes
/// of their own.
/// </summary>
public sealed class ReadyClaimGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the gate; disposing the result lets the next claimer in.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Entry(_semaphore);
    }

    private sealed class Entry(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
