namespace ILD.WorkItemServer.Hosting;

/// <summary>
/// Keeps a backup import and a stale-reclaim pass from ever overlapping. An import replaces
/// every work item with heartbeats dating from the backup, and the pass that would reset
/// them runs only after the import has set them to now; so a pass never starts during an
/// import and an import waits out a pass already running.
///
/// <para>A pass skips when the gate is taken rather than waiting: the next one is thirty
/// seconds away, and waiting would put a pass right after the import's commit.</para>
/// </summary>
public sealed class ImportGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for a pass in progress, then holds the gate until disposed.</summary>
    public async Task<IDisposable> EnterImportAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Release(_gate);
    }

    /// <summary>Holds the gate until disposed, or null when an import (or another pass) has it.</summary>
    public IDisposable? TryEnterPass() => _gate.Wait(0) ? new Release(_gate) : null;

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
