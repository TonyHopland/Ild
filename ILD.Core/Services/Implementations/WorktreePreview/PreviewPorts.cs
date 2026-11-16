using System.Net;
using System.Net.Sockets;

namespace ILD.Core.Services.Implementations;

/// <summary>Chooses the ports a preview's services listen on. <paramref name="isPortAvailable"/> is how a port is probed: binding it on loopback unless a test supplies its own.</summary>
internal sealed class PreviewPorts(Func<int, bool>? isPortAvailable = null)
{
    private readonly Func<int, bool> _isPortAvailable = isPortAvailable ?? IsPortAvailable;

    /// <summary>
    /// Allocates every profile service's port: an override where the caller gave one,
    /// otherwise one the preview chooses (<see cref="ChoosePort"/>). Each is probed once.
    /// </summary>
    public Dictionary<string, int> AllocatePorts(PreviewProfileConfig profile, IReadOnlyDictionary<string, int>? overrides)
    {
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reservedPorts = new HashSet<int>();

        if (overrides != null)
        {
            foreach (var (alias, port) in overrides)
            {
                if (port <= 0)
                    throw new InvalidOperationException($"Preview port override for alias '{alias}' must be greater than zero.");
                if (!reservedPorts.Add(port))
                    throw new InvalidOperationException($"Preview port override '{port}' is assigned more than once.");
            }
        }

        foreach (var service in profile.Services)
        {
            if (ports.ContainsKey(service.Port))
                continue;

            if (overrides != null && overrides.TryGetValue(service.Port, out var overriddenPort))
            {
                ports[service.Port] = ClaimRequestedPort(service.Port, overriddenPort);
                continue;
            }

            var port = ChoosePort(service, reservedPorts);
            ports[service.Port] = port;
            reservedPorts.Add(port);
        }

        return ports;
    }

    /// <summary>
    /// A port the caller asked for by name, refused when something already holds it.
    /// </summary>
    private int ClaimRequestedPort(string alias, int port)
    {
        if (!_isPortAvailable(port))
            throw new InvalidOperationException($"Preview port '{port}' for alias '{alias}' is already in use.");
        return port;
    }

    /// <summary>
    /// A port the preview chooses for a service: its suggested port when that is free
    /// and not <paramref name="reserved"/>, otherwise a free one. The probe that chose it
    /// is the only one: once released, any socket on the host may be handed the port, so
    /// probing it again can only fail the start over a port that was free when chosen.
    /// </summary>
    private int ChoosePort(PreviewServiceConfig service, ISet<int> reserved)
    {
        var suggested = service.SuggestedPort;
        return suggested is > 0 && !reserved.Contains(suggested.Value) && _isPortAvailable(suggested.Value)
            ? suggested.Value
            : FindFreePort(reserved);
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreePort(ISet<int>? reservedPorts = null)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (reservedPorts == null || !reservedPorts.Contains(port))
                    return port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// Allocates a port for a single service whose alias is not already reserved on a
    /// running runtime — the case where a service was added to the config after the
    /// runtime was created. Honours an explicit override, otherwise prefers the
    /// service's suggested port and falls back to a free one.
    /// </summary>
    public void EnsureServicePortAllocated(PreviewServiceConfig service, Dictionary<string, int> ports, IReadOnlyDictionary<string, int>? overrides)
    {
        if (ports.ContainsKey(service.Port))
            return;

        var reserved = new HashSet<int>(ports.Values);
        int port;
        if (overrides != null && overrides.TryGetValue(service.Port, out var overridden))
        {
            if (overridden <= 0)
                throw new InvalidOperationException($"Preview port override for alias '{service.Port}' must be greater than zero.");
            if (reserved.Contains(overridden))
                throw new InvalidOperationException($"Preview port '{overridden}' for alias '{service.Port}' is already in use.");
            port = ClaimRequestedPort(service.Port, overridden);
        }
        else
        {
            port = ChoosePort(service, reserved);
        }

        ports[service.Port] = port;
    }
}
