using System.Net;
using System.Net.Sockets;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>Why the proxy refused to dial a resolved address; <see cref="ProtectedId"/> names the switchable protection, null for loopback.</summary>
public sealed record DestinationRefusal(string Reason, string? ProtectedId)
{
    public const string LoopbackReason = "loopback";

    public static readonly DestinationRefusal Loopback = new(LoopbackReason, null);
}

/// <summary>
/// What the egress proxy refuses by the addresses it resolved, not by the name asked for (<c>docs/adr/0036</c>):
/// loopback always, ILD's own infrastructure while the operator has not switched that protection off.
/// </summary>
public static class EgressDestinationGuard
{
    public const string RunIsolationReason = "another run's network";

    /// <summary>Loopback, IPv4-mapped loopback, and the unspecified addresses, which connect to the machine itself.</summary>
    public static bool IsLoopback(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);
    }

    /// <param name="isolated">
    /// The run networks and pods: refused like loopback, whatever the operator has switched off and even for a declared forward, because
    /// the proxy is on every run's network and an agent could otherwise reach another run through it.
    /// </param>
    public static DestinationRefusal? Check(IEnumerable<IPAddress> resolved, IReadOnlyList<ProtectedDestination> protectedDestinations, bool exemptFromProtections,
        IReadOnlyList<IpRange>? isolated = null)
    {
        var addresses = resolved.Select(Normalize).ToList();
        if (addresses.Any(IsLoopback)) return DestinationRefusal.Loopback;
        if (isolated is { Count: > 0 } && addresses.Any(a => isolated.Any(r => r.Contains(a))))
        {
            // ILD has an address on every run's network; a refusal of that one is named for what it is.
            var own = protectedDestinations.FirstOrDefault(d => d.Id == ProtectedDestination.Ild && addresses.Any(a => d.Covers(a)));
            return own is null ? new DestinationRefusal(RunIsolationReason, null) : new DestinationRefusal(own.RefusalReason, own.Id);
        }
        if (exemptFromProtections) return null;

        foreach (var destination in protectedDestinations)
            if (destination.Enabled && addresses.Any(a => destination.Covers(a)))
                return new DestinationRefusal(destination.RefusalReason, destination.Id);
        return null;
    }

    /// <summary>
    /// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> refuses the
    /// unspecified addresses as input, so an IP literal is taken as it stands.
    /// </summary>
    public static Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? Task.FromResult(new[] { literal }) : Dns.GetHostAddressesAsync(host, ct);

    public static async Task<TcpClient> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(addresses, port, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>An address range in CIDR form, IPv4 or IPv6; a single address is its /32 or /128.</summary>
public readonly record struct IpRange(IPAddress Network, int Prefix)
{
    public static IpRange Single(IPAddress address) => new(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

    public static bool TryParse(string cidr, out IpRange range)
    {
        range = default;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix)) return false;
        if (prefix < 0 || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)) return false;
        range = new IpRange(address, prefix);
        return true;
    }

    /// <summary>The CIDR text, or the bare address for a range of one address.</summary>
    public override string ToString()
        => Prefix == (Network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128) ? Network.ToString() : $"{Network}/{Prefix}";

    public static IpRange Parse(string cidr) => TryParse(cidr, out var range) ? range : throw new FormatException($"'{cidr}' is not a CIDR range.");

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var network = Network.IsIPv4MappedToIPv6 ? Network.MapToIPv4() : Network;
        if (address.AddressFamily != network.AddressFamily) return false;
        var (a, n) = (address.GetAddressBytes(), network.GetAddressBytes());
        var prefix = Network.IsIPv4MappedToIPv6 && network.AddressFamily == AddressFamily.InterNetwork ? Prefix - 96 : Prefix;
        var whole = prefix / 8;
        for (var i = 0; i < whole; i++)
            if (a[i] != n[i]) return false;
        if (prefix % 8 == 0) return true;
        var mask = (byte)(0xFF << (8 - prefix % 8));
        return (a[whole] & mask) == (n[whole] & mask);
    }
}
