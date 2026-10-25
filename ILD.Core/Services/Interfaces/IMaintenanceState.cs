namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Whether the instance is replacing its own data. While it is, the background services skip their
/// passes, runs do not launch and the API answers 503: whatever they would read or write is about
/// to be replaced, and a pass that slipped in would act on half a restore.
///
/// <para>Work that is already under way when the restore begins is waited for: everything that
/// reads or writes the data holds a <see cref="TryBeginWork"/> lease while it does, and a restore
/// starts only once the leases are gone.</para>
/// </summary>
public interface IMaintenanceState
{
    bool IsActive { get; }

    /// <summary>
    /// A lease for a request or a pass that touches the data, or null when a restore has begun and the
    /// work must not start. Dispose it when the work is done.
    /// </summary>
    IDisposable? TryBeginWork();
}

/// <summary>The one flag, registered as a singleton so every consumer shares it.</summary>
public sealed class MaintenanceState : IMaintenanceState
{
    /// <summary>
    /// A flag nobody can raise, so components taking <see cref="IMaintenanceState"/> as an optional
    /// constructor parameter behave as before when nobody injects one (tests, direct construction).
    /// </summary>
    public static readonly IMaintenanceState Never = new MaintenanceState();

    private int _active;
    private int _work;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    private readonly TimeSpan _drainTimeout;

    /// <param name="drainTimeout">How long a restore waits for work already under way before giving up; 30 seconds by default.</param>
    public MaintenanceState(TimeSpan? drainTimeout = null) => _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Counted before the flag is read, and the flag is raised before the count is read by
    /// <see cref="WaitForWorkAsync"/>: work either sees the flag and does not start, or is counted
    /// and waited for. There is no third case.
    /// </summary>
    public IDisposable? TryBeginWork()
    {
        Interlocked.Increment(ref _work);
        var lease = new WorkLease(this);
        if (!IsActive) return lease;
        lease.Dispose();
        return null;
    }

    /// <summary>
    /// Raises the flag until the returned lease is disposed. Only one restore at a time may do so, which the
    /// caller guarantees; a second is a bug and throws.
    /// </summary>
    public IDisposable Enter()
        => Interlocked.CompareExchange(ref _active, 1, 0) == 0
            ? new Lease(this)
            : throw new InvalidOperationException("The instance is already in maintenance.");

    /// <summary>
    /// Waits until no more than <paramref name="allowed"/> leases are held (the request that asks is
    /// itself one) and says whether that happened within the drain timeout.
    /// </summary>
    public async Task<bool> WaitForWorkAsync(int allowed, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_drainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (Volatile.Read(ref _work) > allowed)
                await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Volatile.Read(ref _work) <= allowed;
        }
    }

    private sealed class Lease(MaintenanceState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Volatile.Write(ref state._active, 0);
        }
    }

    private sealed class WorkLease(MaintenanceState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref state._work);
        }
    }
}
