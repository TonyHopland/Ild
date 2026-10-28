using System.Net;
using System.Net.Sockets;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The address check the egress proxy applies after resolving a destination,
/// whatever the mode and lists say: private, loopback, link-local and similar
/// addresses are the orchestrator's own network, not the agent's, so they are
/// refused unless an operator allows them in <c>network.internalAllow</c>.
///
/// <para>
/// An allow pattern is an exact host, a leading-dot suffix (or <c>*.suffix</c>),
/// an IP literal or a CIDR. A host pattern permits every address the matching
/// name resolves to; an address pattern permits the addresses it contains, with
/// an IPv4-mapped IPv6 address compared as IPv4.
/// </para>
/// </summary>
public static class EgressAddressRules
{
    public const int MaxStoredLength = 4096;

    public static readonly IPNetwork[] Blocked =
    {
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    };

    public static bool IsBlocked(IPAddress address)
    {
        var unmapped = Unmap(address);
        return Blocked.Any(range => range.Contains(unmapped));
    }

    /// <summary>
    /// Whether the proxy may connect to <paramref name="address"/> for a request
    /// naming <paramref name="host"/>: it is not in a blocked range, or one of
    /// the operator's <paramref name="patterns"/> allows it.
    /// </summary>
    public static bool Permits(IReadOnlyList<string> patterns, string host, IPAddress address)
    {
        if (!IsBlocked(address)) return true;

        var unmapped = Unmap(address);
        var canonicalHost = EgressRules.NormalizeHost(host);
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('/'))
            {
                if (IPNetwork.TryParse(pattern, out var network) && network.Contains(unmapped)) return true;
            }
            else if (IPAddress.TryParse(pattern, out var literal))
            {
                if (Unmap(literal).Equals(unmapped)) return true;
            }
            else if (EgressRules.Matches(pattern, canonicalHost))
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryNormalizeInternalPattern(string input, out string pattern, out string error)
    {
        if (!input.Contains('/'))
            return EgressRules.TryNormalizePattern(input, out pattern, out error);

        error = string.Empty;
        var slash = input.IndexOf('/');
        if (IPAddress.TryParse(input[..slash], out var address)
            && IPNetwork.TryParse(input, out var network)
            && network.BaseAddress.Equals(address))
        {
            pattern = network.ToString();
            return true;
        }
        pattern = string.Empty;
        error = $"'{input}' is not a valid CIDR (use an address with no host bits set, e.g. 10.0.0.0/8)";
        return false;
    }

    /// <summary>
    /// Validate and canonicalise the whole setting value: newline- or comma-separated
    /// patterns, trimmed, without blanks or duplicates, stored joined by newlines.
    /// </summary>
    public static bool TryNormalizeInternalAllow(string? raw, out string canonical, out string error)
    {
        canonical = string.Empty;
        error = string.Empty;

        var patterns = new List<string>();
        foreach (var item in (raw ?? string.Empty).Split(new[] { '\n', '\r', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryNormalizeInternalPattern(item, out var pattern, out var reason))
            {
                error = $"'{item}': {reason}";
                return false;
            }
            if (!patterns.Contains(pattern)) patterns.Add(pattern);
        }

        var joined = string.Join('\n', patterns);
        if (joined.Length > MaxStoredLength)
        {
            error = $"The list is too long: at most {MaxStoredLength} characters once saved";
            return false;
        }
        canonical = joined;
        return true;
    }

    private static IPAddress Unmap(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
