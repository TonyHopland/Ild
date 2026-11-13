using System.Diagnostics;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>A process of this machine, as the run host hands it out.</summary>
internal sealed class LocalRunProcess : IRunProcess
{
    private readonly Process _process;
    private readonly Action<LocalRunProcess> _released;
    private int _released_;

    public LocalRunProcess(Process process, Action<LocalRunProcess> released)
    {
        _process = process;
        _released = released;
    }

    public int Id => _process.Id;

    internal bool HasExitedSafe()
    {
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;
    public StreamWriter StandardInput => _process.StandardInput;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    public bool TryKill() => TryKill(out _);

    public bool TryKill(out string? failure)
    {
        failure = null;
        try
        {
            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Already exited — that is the outcome we wanted anyway.
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released_, 1) == 0)
            _released(this);
        _process.Dispose();
    }
}
