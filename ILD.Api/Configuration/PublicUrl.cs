using ILD.Core.Services.Implementations;

namespace ILD.Api.Configuration;

/// <summary>
/// The address callers outside ILD reach it at, for what ILD tells them about itself. <c>ILD_PUBLIC_URL</c> names it;
/// otherwise a request from a trusted proxy (<c>ILD_TRUSTED_PROXIES</c>) is read from its <c>X-Forwarded-Proto</c> and
/// <c>X-Forwarded-Host</c>, and any other request from its own scheme and host.
/// </summary>
public static class PublicUrl
{
    public const string EnvVar = "ILD_PUBLIC_URL";

    public static string For(HttpRequest request, IConfiguration configuration, TrustedProxies proxies)
    {
        var configured = Environment.GetEnvironmentVariable(EnvVar) ?? configuration[EnvVar];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            return url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath.TrimEnd('/');

        var scheme = request.Scheme;
        var host = request.Host.Value;
        if (proxies.IsTrustedProxy(request.HttpContext.Connection.RemoteIpAddress))
        {
            if (First(request.Headers["X-Forwarded-Proto"]) is { } proto && proto is "http" or "https") scheme = proto;
            if (First(request.Headers["X-Forwarded-Host"]) is { } forwardedHost) host = forwardedHost;
        }
        return $"{scheme}://{host}{request.PathBase}";
    }

    private static string? First(Microsoft.Extensions.Primitives.StringValues values)
        => values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
