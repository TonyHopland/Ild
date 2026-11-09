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
    /// <summary>Loopback, IPv4-mapped loopback, and the unspecified addresses, which connect to the machine itself.</summary>
    public static bool IsLoopback(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);
    }

    public static DestinationRefusal? Check(IEnumerable<IPAddress> resolved, IReadOnlyList<ProtectedDestination> protectedDestinations, bool exemptFromProtections)
    {
        var addresses = resolved.Select(Normalize).ToList();
        if (addresses.Any(IsLoopback)) return DestinationRefusal.Loopback;
        if (exemptFromProtections) return null;

        foreach (var destination in protectedDestinations)
            if (destination.Enabled && addresses.Any(a => destination.Addresses.Any(p => Normalize(p).Equals(a))))
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
