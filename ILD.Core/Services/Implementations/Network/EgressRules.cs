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

        if (!LabelsAreValid(host))
        {
            error = "Not a valid host name: use letters, digits, hyphens, underscores and dots (a leading dot matches every subdomain)";
            return false;
        }

        pattern = suffix ? "." + host : host;
        return true;
    }

    /// <summary>
    /// Validate and canonicalise a forward's destination. Unlike a list pattern
    /// this has to name one place: a leading-dot or wildcard form covers a set of
    /// hosts, and there is nothing to connect a socket to in a set.
    /// </summary>
    public static bool TryNormalizeForwardHost(string? input, out string host, out string error)
    {
        host = string.Empty;
        error = string.Empty;

        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "Enter the destination host, e.g. postgres or db.internal";
            return false;
        }
        if (raw.Contains("://", StringComparison.Ordinal) || raw.Contains('/'))
        {
            error = "Enter a host name, not a URL";
            return false;
        }
        if (raw.StartsWith('.') || raw.StartsWith("*.", StringComparison.Ordinal))
        {
            error = "A forward needs one destination, not a pattern: drop the leading dot or *";
            return false;
        }

        var canonical = NormalizeHost(raw);
        if (canonical.Length == 0)
        {
            error = "Enter the destination host, e.g. postgres or db.internal";
            return false;
        }
        if (canonical.Length > MaxHostLength)
        {
            error = $"Host names are at most {MaxHostLength} characters";
            return false;
        }

        if (IPAddress.TryParse(canonical, out var ip))
        {
            host = ip.ToString();
            return true;
        }
        if (canonical.Contains(':'))
        {
            error = "Enter the host on its own; the port has its own field";
            return false;
        }
        if (!LabelsAreValid(canonical))
        {
            error = "Not a valid host name: use letters, digits, hyphens, underscores and dots";
            return false;
        }

        host = canonical;
        return true;
    }

    private static bool LabelsAreValid(string host)
        => host.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));

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
