using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The public origin worktree previews are served from, parsed once from
/// <c>ILD_PREVIEW_PROXY_BASE</c> (scheme + host + optional port, e.g.
/// <c>http://ild.localhost:8080</c>). It answers the two questions the preview
/// proxy is built on, and it is the only place either is answered:
/// <list type="bullet">
/// <item>Given an inbound <c>Host</c>, is this a preview request, and for which
/// host label? (<see cref="TryGetHostLabel"/>)</item>
/// <item>Given a host label, what URL do we advertise to a human?
/// (<see cref="BuildUrl"/>)</item>
/// </list>
/// <para>
/// The variable doubles as the feature's opt-in gate: when it is unset the base
/// is <see cref="Enabled"/>=false, the proxy middleware forwards nothing, and
/// preview public URLs keep their historical <c>http://{publicHost}:{port}</c>
/// shape. That matters because proxied previews are <em>unauthenticated</em> —
/// see <c>docs/deployment.md</c>.
/// </para>
/// <para>
/// Matching deliberately requires a non-empty label followed by a dot: the apex
/// host itself (<c>ild.kube</c>) is not a preview host, so the main UI served on
/// it is never routed into a worktree.
/// </para>
/// </summary>
public sealed class PreviewProxyBase
{
    /// <summary>Configuration key (and environment variable) the base is read from.</summary>
    public const string ConfigurationKey = "ILD_PREVIEW_PROXY_BASE";

    private static readonly PreviewProxyBase DisabledBase = new(null);

    private PreviewProxyBase(string? configurationError)
    {
        Scheme = string.Empty;
        Host = string.Empty;
        ConfigurationError = configurationError;
    }

    private PreviewProxyBase(string scheme, string host, int? port)
    {
        Enabled = true;
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    /// <summary>False when <c>ILD_PREVIEW_PROXY_BASE</c> is unset or unusable; the proxy is then inert.</summary>
    public bool Enabled { get; }

    /// <summary>Scheme previews are advertised on (<c>http</c> when the value omitted one).</summary>
    public string Scheme { get; }

    /// <summary>Apex host previews are served under, e.g. <c>ild.kube</c>. Empty when disabled.</summary>
    public string Host { get; }

    /// <summary>Explicit port in the advertised URL, or null when the scheme's default port applies.</summary>
    public int? Port { get; }

    /// <summary>
    /// Set when a non-empty <c>ILD_PREVIEW_PROXY_BASE</c> could not be parsed, so
    /// callers can log why previews silently stayed on their loopback URLs rather
    /// than leaving an operator to guess. Null otherwise.
    /// </summary>
    public string? ConfigurationError { get; }

    /// <summary>An inert base — the shape every consumer sees when the feature is off.</summary>
    public static PreviewProxyBase Disabled => DisabledBase;

    public static PreviewProxyBase FromConfiguration(IConfiguration configuration)
        => Parse(configuration[ConfigurationKey]);

    /// <summary>
    /// Parses the configured value. A bare authority (<c>ild.kube:8080</c>) is
    /// accepted and assumed to be <c>http</c>; anything unparseable yields a
    /// disabled base carrying <see cref="ConfigurationError"/> rather than
    /// throwing, so a typo degrades previews instead of failing startup.
    /// </summary>
    public static PreviewProxyBase Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Disabled;

        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new PreviewProxyBase(
                $"'{value}' is not a usable preview proxy base; expected scheme://host[:port], e.g. http://ild.kube.");
        }

        return new PreviewProxyBase(uri.Scheme, uri.Host, uri.IsDefaultPort ? null : uri.Port);
    }

    /// <summary>
    /// True when <paramref name="requestHost"/> (a <c>Host</c> header with its port
    /// already stripped) is <c>&lt;label&gt;.&lt;Host&gt;</c>, yielding that label.
    /// The apex host and any unrelated host return false — those requests belong to
    /// the main UI and must travel the pipeline untouched.
    /// </summary>
    public bool TryGetHostLabel(string? requestHost, out string label)
    {
        label = string.Empty;
        if (!Enabled || string.IsNullOrEmpty(requestHost))
            return false;

        // A label needs at least one character plus the separating dot.
        var labelLength = requestHost.Length - Host.Length - 1;
        if (labelLength <= 0)
            return false;

        if (requestHost[labelLength] != '.')
            return false;

        if (string.Compare(requestHost, labelLength + 1, Host, 0, Host.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        label = requestHost[..labelLength];
        return true;
    }

    /// <summary>Builds the advertised URL for a host label, e.g. <c>http://wi-12.ild.kube</c>.</summary>
    public string BuildUrl(string label)
        => Port is int port
            ? $"{Scheme}://{label}.{Host}:{port}"
            : $"{Scheme}://{label}.{Host}";
}
