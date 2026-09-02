using System.Net;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The allow/deny rule the egress proxy applies, with nothing else attached, so
/// it can be reasoned about (and tested) as a function of mode, lists and one
/// destination.
///
/// <para>
/// A list entry is a host pattern: an exact host (<c>api.example.com</c>) or a
/// leading-dot suffix (<c>.example.com</c>), which covers <c>example.com</c> and
/// every host beneath it. <c>*.example.com</c> is accepted as the same suffix.
/// Comparison is case-insensitive and ignores a trailing dot. An entry with an
/// <see cref="NetworkPolicyEntry.AiProviderId"/> applies only to connections the
/// agent made on behalf of that provider; one without applies to all.
/// </para>
/// </summary>
public static class EgressRules
{
    public const int MaxHostLength = 253;

    public static bool TryParseMode(string? value, out NetworkMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "off": mode = NetworkMode.Off; return true;
            case "whitelist": mode = NetworkMode.Whitelist; return true;
            case "blacklist": mode = NetworkMode.Blacklist; return true;
            default: mode = NetworkMode.Off; return false;
        }
    }

    public static string ModeName(NetworkMode mode) => mode switch
    {
        NetworkMode.Whitelist => "whitelist",
        NetworkMode.Blacklist => "blacklist",
        _ => "off",
    };

    /// <summary>
    /// Canonical form of a host as it appears on the wire: lower-case, no
    /// trailing dot, IPv6 brackets removed.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length > 1 && h[0] == '[' && h[^1] == ']')
            h = h[1..^1];
        return h;
    }

    /// <summary>
    /// Validate and canonicalise a pattern typed by an operator. Rejects URLs,
    /// ports and anything that is not a host name or IP literal, so a typo
    /// cannot become an entry that never matches.
    /// </summary>
    public static bool TryNormalizePattern(string? input, out string pattern, out string error)
    {
        pattern = string.Empty;
        error = string.Empty;

        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "Enter a host name, e.g. api.example.com or .example.com";
            return false;
        }
        if (raw.Contains("://", StringComparison.Ordinal) || raw.Contains('/'))
        {
            error = "Enter a host name, not a URL";
            return false;
        }

        if (raw.StartsWith("*.", StringComparison.Ordinal))
            raw = raw[1..];

        var suffix = raw.StartsWith('.');
        var host = NormalizeHost(suffix ? raw[1..] : raw);

        if (host.Length == 0)
        {
            error = "A suffix needs a domain after the dot, e.g. .example.com";
            return false;
        }
        if (host.Length > MaxHostLength - (suffix ? 1 : 0))
        {
            error = $"Host names are at most {MaxHostLength} characters";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (suffix)
            {
                error = "A suffix pattern must be a domain, not an IP address";
                return false;
            }
            pattern = ip.ToString();
            return true;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-'
                || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                error = "Not a valid host name: use letters, digits, hyphens and dots (a leading dot matches every subdomain)";
                return false;
            }
        }

        pattern = suffix ? "." + host : host;
        return true;
    }

    /// <summary>Whether a canonical pattern covers a canonical host.</summary>
    public static bool Matches(string pattern, string host)
    {
        if (pattern.Length == 0) return false;
        if (pattern[0] != '.')
            return string.Equals(pattern, host, StringComparison.Ordinal);

        return host.Length == pattern.Length - 1
            ? pattern.AsSpan(1).SequenceEqual(host)
            : host.EndsWith(pattern, StringComparison.Ordinal);
    }

    public static NetworkDecision Decide(
        NetworkMode mode,
        IEnumerable<NetworkPolicyEntry> entries,
        string host,
        Guid? aiProviderId)
    {
        if (mode == NetworkMode.Off)
            return NetworkDecision.Advisory;

        var canonical = NormalizeHost(host);
        var wanted = mode == NetworkMode.Whitelist ? NetworkListKind.Whitelist : NetworkListKind.Blacklist;
        var listed = entries.Any(e =>
            e.ListKind == wanted
            && (e.AiProviderId is null || e.AiProviderId == aiProviderId)
            && Matches(e.Host, canonical));

        return mode == NetworkMode.Whitelist
            ? (listed ? NetworkDecision.Allowed : NetworkDecision.Blocked)
            : (listed ? NetworkDecision.Blocked : NetworkDecision.Allowed);
    }
}
