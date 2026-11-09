using System.Net.Sockets;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Checks that a run's network really has no way out but the proxy (<c>docs/adr/0036</c>): an environment tries a direct
/// connection to an address ILD itself reaches, and the verdict is <c>enforced</c> only when that fails. Nothing is
/// asserted before a probe has run, and a probe that cannot be made (ILD reaches nothing outside, the environment
/// cannot run it) leaves the verdict as it was.
/// </summary>
public sealed class NetworkEnforcementProbe
{
    public const string TargetSetting = "ILD_NETWORK_PROBE_TARGET";
    public const string DefaultTarget = "1.1.1.1:443";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly string _target;
    private readonly TimeProvider _clock;
    private readonly Func<string, int, CancellationToken, Task<bool>> _ildReaches;
    private int _running;
    private (bool Open, long At)? _last;

    public NetworkEnforcementProbe(Func<string, string?> environment, TimeProvider? clock = null, Func<string, int, CancellationToken, Task<bool>>? ildReaches = null)
    {
        _target = string.IsNullOrWhiteSpace(environment(TargetSetting)) ? DefaultTarget : environment(TargetSetting)!.Trim();
        _clock = clock ?? TimeProvider.System;
        _ildReaches = ildReaches ?? ReachesAsync;
    }

    /// <summary>True when a probe connected out directly, false when it could not, null before any probe has answered.</summary>
    public bool? EscapeObserved => _last?.Open;

    /// <summary>
    /// Runs one probe unless one ran within <see cref="Interval"/> or is running. <paramref name="connectFromRun"/> tries the
    /// connection from inside an environment: true when it connected, false when it was refused or dropped, null when it
    /// could not tell.
    /// </summary>
    public async Task ProbeAsync(Func<string, int, CancellationToken, Task<bool?>> connectFromRun, CancellationToken ct = default)
    {
        if (_last is { } last && _clock.GetElapsedTime(last.At) < Interval) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            if (!TrySplit(_target, out var host, out var port)) return;
            if (!await _ildReaches(host, port, ct).ConfigureAwait(false)) return;
            if (await connectFromRun(host, port, ct).ConfigureAwait(false) is { } open)
                _last = (open, _clock.GetTimestamp());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static bool TrySplit(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var colon = target.LastIndexOf(':');
        if (colon <= 0) return false;
        host = target[..colon].Trim('[', ']');
        return int.TryParse(target[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }

    private static async Task<bool> ReachesAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
