using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>
/// The loopback address each forward host answers on in a run's or chat's environment (docs/adr/0020). The hosts file
/// maps a name to one address, so the address belongs to the host name and forwards to one host on several ports
/// share it. It comes from the name, so it is the same on every sync and in every environment, and lies in
/// <c>127.1.0.0/16</c>, which leaves <c>127.0.0.1</c> to the run's own services and Docker's resolver at
/// <c>127.0.0.11</c> alone.
/// </summary>
internal static class ForwardAddresses
{
    private const int Space = 1 << 16;

    /// <summary>
    /// An address for each distinct host, compared without regard to case. A host that is an IP address needs no name
    /// to map and gets none. Two hosts that hash to one address are told apart by probing on, in sorted host order,
    /// so the same set of hosts always gets the same addresses.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Allocate(IEnumerable<string> hosts)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<int>();
        foreach (var host in hosts.Where(h => !IPAddress.TryParse(h, out _)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var slot = Start(host);
            while (!Usable(slot) || !taken.Add(slot)) slot = (slot + 1) % Space;
            result[host] = $"127.1.{slot >> 8}.{slot & 0xFF}";
        }
        return result;
    }

    private static int Start(string host)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant()));
        return hash[0] << 8 | hash[1];
    }

    // The network and broadcast addresses of each /24 are left unused.
    private static bool Usable(int slot) => (slot & 0xFF) is not (0 or 255);
}
