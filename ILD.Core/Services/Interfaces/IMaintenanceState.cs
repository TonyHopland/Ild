namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Whether the instance is replacing its own data. While it is, the background services skip their
/// passes, runs do not launch and the API answers 503: whatever they would read or write is about
/// to be replaced, and a pass that slipped in would act on half a restore.
/// </summary>
public interface IMaintenanceState
{
    bool IsActive { get; }
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

    public bool IsActive => Volatile.Read(ref _active) == 1;

    /// <summary>Raises the flag until the returned lease is disposed, or null when it is already raised.</summary>
    public IDisposable? TryEnter()
        => Interlocked.CompareExchange(ref _active, 1, 0) == 0 ? new Lease(this) : null;

    private sealed class Lease(MaintenanceState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Volatile.Write(ref state._active, 0);
        }
    }
}
