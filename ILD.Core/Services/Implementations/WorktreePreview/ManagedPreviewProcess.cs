using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations;

internal sealed class ManagedPreviewProcess
{
    public ManagedPreviewProcess(
        PreviewServiceConfig service,
        IRunProcess process,
        StreamWriter writer,
        SemaphoreSlim writeGate,
        Task stdOutPump,
        Task stdErrPump,
        string logFilePath,
        PackageFeedFileShare.Lease feedLease)
    {
        FeedLease = feedLease;
        Service = service;
        Process = process;
        Writer = writer;
        WriteGate = writeGate;
        StdOutPump = stdOutPump;
        StdErrPump = stdErrPump;
        LogFilePath = logFilePath;
    }

    public PreviewServiceConfig Service { get; }
    public IRunProcess Process { get; }
    public StreamWriter Writer { get; }
    public SemaphoreSlim WriteGate { get; }
    public Task StdOutPump { get; }
    public Task StdErrPump { get; }
    public string LogFilePath { get; }

    /// <summary>This service's hold on the runtime's feed credential file, let go when it exits.</summary>
    public PackageFeedFileShare.Lease FeedLease { get; }

    private volatile bool _stopped;

    /// <summary>
    /// Records that the teardown path has taken this process over, before it
    /// disposes <see cref="Process"/>.
    /// </summary>
    public void MarkStopped() => _stopped = true;

    /// <summary>
    /// Whether this service is up — safe to ask from any thread at any time, which
    /// <see cref="Process"/> is not: reading <c>HasExited</c> on a disposed
    /// <see cref="System.Diagnostics.Process"/> throws
    /// <see cref="InvalidOperationException"/>, and teardown can dispose it at any
    /// moment. The preview proxy asks this on every request to a preview hostname
    /// without holding the service's start/stop gate, so it cannot assume a stop in
    /// flight has either not begun or fully finished. Callers that do hold the gate
    /// may read <see cref="Process"/> directly, and do, to report exit codes.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (_stopped)
                return false;

            try
            {
                return !Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Disposed between the flag check and here.
                return false;
            }
        }
    }
}
