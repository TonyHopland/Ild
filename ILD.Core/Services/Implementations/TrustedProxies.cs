using System.Net;
using ILD.Core.Services.Implementations.Network;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-For</c> header ILD believes (<c>ILD_TRUSTED_PROXIES</c>). Behind an ingress every
/// connection comes from the ingress's own address, so a caller is told apart only by what the ingress says about it.
/// The header is read from a trusted proxy alone: anyone else can write whatever it likes into it.
/// </summary>
public sealed class TrustedProxies
{
    public const string EnvVar = "ILD_TRUSTED_PROXIES";

    private readonly IReadOnlyList<IpRange> _ranges;

    public TrustedProxies(IEnumerable<IpRange> ranges)
    {
        _ranges = ranges.ToList();
    }

    public static TrustedProxies None { get; } = new([]);

    /// <summary>Reads a comma-separated list of addresses and CIDR ranges. An entry that is neither stops ILD from starting.</summary>
    public static TrustedProxies Parse(string? value)
    {
        var ranges = new List<IpRange>();
        foreach (var entry in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IpRange.TryParse(entry, out var range)) ranges.Add(range);
            else if (IPAddress.TryParse(entry, out var address)) ranges.Add(IpRange.Single(address));
            else throw new InvalidOperationException($"{EnvVar} has '{entry}', which is neither an address nor a CIDR range.");
        }
        return new TrustedProxies(ranges);
    }

    /// <summary>
    /// The caller's address: <paramref name="remote"/>, or when that is a trusted proxy the nearest address in
    /// <paramref name="forwardedFor"/> that is not one. A header that cannot be read leaves <paramref name="remote"/>.
    /// </summary>
    public IPAddress? ClientOf(IPAddress? remote, IEnumerable<string?> forwardedFor)
    {
        if (remote is null || !IsTrusted(remote)) return remote;

        var hops = forwardedFor
            .SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            if (!IPAddress.TryParse(hops[i], out var hop)) return remote;
            if (!IsTrusted(hop)) return hop.IsIPv4MappedToIPv6 ? hop.MapToIPv4() : hop;
        }
        return remote;
    }

    private bool IsTrusted(IPAddress address) => _ranges.Any(range => range.Contains(address));
}
