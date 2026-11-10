using System.Net;
using System.Net.Sockets;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>
/// The IPv4 arithmetic behind giving each container network a subnet of its own: Docker only accepts a fixed address on a network whose
/// subnet was chosen, and its default pools run out after about 30 networks.
/// </summary>
internal static class NetworkSubnets
{
    /// <summary>The prefix length of the subnet each network gets: 16 addresses, which the gateway, ILD and the one container need.</summary>
    public const int NetworkPrefix = 28;

    /// <summary>The widest pool accepted is a /24; anything narrower holds too few networks to be a pool.</summary>
    public const int NarrowestPoolPrefix = 24;

    public static bool TryParse(string cidr, out (uint Start, int Prefix) subnet)
    {
        subnet = default;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            return false;
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        subnet = (value & Mask(prefix), prefix);
        return true;
    }

    public static string Format(uint address) => $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    public static string Cidr(uint start, int prefix) => $"{Format(start)}/{prefix}";

    /// <summary>The address of the first host in the subnet, which the engine takes for its gateway.</summary>
    public static string Gateway(uint start) => Format(start + 1);

    public static bool Overlaps((uint Start, int Prefix) a, (uint Start, int Prefix) b)
    {
        var (aEnd, bEnd) = (End(a), End(b));
        return a.Start <= bEnd && b.Start <= aEnd;
    }

    /// <summary>The subnets of <see cref="NetworkPrefix"/> in the pool, in order.</summary>
    public static IEnumerable<uint> Candidates((uint Start, int Prefix) pool)
    {
        var size = 1UL << (32 - NetworkPrefix);
        for (var start = (ulong)pool.Start; start <= End(pool); start += size)
            yield return (uint)start;
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    private static ulong End((uint Start, int Prefix) subnet) => subnet.Start + (1UL << (32 - subnet.Prefix)) - 1;
}
