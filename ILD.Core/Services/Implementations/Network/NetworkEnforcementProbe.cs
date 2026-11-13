using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Checks that a run's network really has no way out but the proxy (<c>docs/adr/0036</c>): an environment tries a direct
/// connection to addresses ILD itself reaches — one outside (<see cref="TargetSetting"/>), and what ILD protects inside: its
/// database and, in a cluster, the API server — and the verdict is <c>enforced</c> only when every one fails. Run environments
/// and the chat environment are probed apart, and any of them connecting makes the verdict open. Nothing is asserted before a
/// probe has run, and a probe that cannot be made (ILD reaches nothing, the environment cannot run it) leaves the verdict as it was.
/// </summary>
public sealed class NetworkEnforcementProbe
{
    public const string TargetSetting = "ILD_NETWORK_PROBE_TARGET";
    public const string DefaultTarget = "1.1.1.1:443";
    public const string RunScope = "run";
    public const string ChatScope = "chat";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<string> _targets;
    private readonly TimeProvider _clock;
    private readonly Func<string, int, CancellationToken, Task<bool>> _ildReaches;
    private readonly Func<string, CancellationToken, Task<string?>> _resolve;
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly ConcurrentDictionary<string, (bool Open, long At)> _last = new();

    public NetworkEnforcementProbe(Func<string, string?> environment, TimeProvider? clock = null, Func<string, int, CancellationToken, Task<bool>>? ildReaches = null,
        Func<string, CancellationToken, Task<string?>>? resolve = null)
    {
        _targets = TargetsOf(environment);
        _clock = clock ?? TimeProvider.System;
        _ildReaches = ildReaches ?? ReachesAsync;
        _resolve = resolve ?? ResolveAsync;
    }

    /// <summary>
    /// What the probe tries: the outside target, the database ILD uses, and the Kubernetes API server when ILD runs in a cluster.
    /// The inside ones matter most: a network that blocks the internet can still leave ILD's own infrastructure open.
    /// </summary>
    private static List<string> TargetsOf(Func<string, string?> environment)
    {
        var targets = new List<string> { string.IsNullOrWhiteSpace(environment(TargetSetting)) ? DefaultTarget : environment(TargetSetting)!.Trim() };
        if (DatabaseTarget(environment("ILD_DB_CONNECTION_STRING")) is { } database) targets.Add(database);
        if (environment("KUBERNETES_SERVICE_HOST") is { Length: > 0 } host)
            targets.Add($"{host.Trim()}:{(int.TryParse(environment("KUBERNETES_SERVICE_PORT"), out var port) && port is > 0 and <= 65535 ? port : 443)}");
        return targets.Distinct().ToList();
    }

    private static string? DatabaseTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            return null;
        }
        var host = (builder.TryGetValue("Host", out var h) ? h : builder.TryGetValue("Server", out var server) ? server : null)?.ToString()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(host)) return null;
        var port = builder.TryGetValue("Port", out var p) && int.TryParse(p?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 5432;
        return $"{host}:{port}";
    }

    /// <summary>True when a probe connected out directly from any environment, false when every probe was refused, null before any has answered.</summary>
    public bool? EscapeObserved => _last.IsEmpty ? null : _last.Values.Any(v => v.Open);

    /// <summary>Whether <paramref name="scope"/> has a verdict that is still fresh.</summary>
    public bool Probed(string scope) => _last.TryGetValue(scope, out var last) && _clock.GetElapsedTime(last.At) < Interval;

    /// <summary>
    /// Runs one probe of <paramref name="scope"/> unless one ran within <see cref="Interval"/> or is running. <paramref name="connectFromRun"/>
    /// tries a connection from inside an environment: true when it connected, false when it was refused or dropped, null when it
    /// could not tell. Open when any target connects; closed when some answered and none connected.
    /// </summary>
    public async Task ProbeAsync(Func<string, int, CancellationToken, Task<bool?>> connectFromRun, CancellationToken ct = default, string scope = RunScope)
    {
        if (Probed(scope)) return;
        if (!_running.TryAdd(scope, 0)) return;
        try
        {
            var open = false;
            var refused = false;
            foreach (var target in _targets)
            {
                if (!TrySplit(target, out var host, out var port)) continue;
                if (await _resolve(host, ct).ConfigureAwait(false) is not { } address) continue;
                if (!await _ildReaches(address, port, ct).ConfigureAwait(false)) continue;
                switch (await connectFromRun(address, port, ct).ConfigureAwait(false))
                {
                    case true:
                        open = true;
                        break;
                    case false:
                        refused = true;
                        break;
                }
                if (open) break;
            }
            if (open || refused) _last[scope] = (open, _clock.GetTimestamp());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        finally
        {
            _running.TryRemove(scope, out _);
        }
    }

    // The environment probed has no DNS, so a name is resolved here and the address is what both sides try.
    private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return (addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault())?.ToString();
        }
        catch (SocketException)
        {
            return null;
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
