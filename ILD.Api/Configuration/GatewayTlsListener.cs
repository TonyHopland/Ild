using ILD.Core.Services.Implementations.ModelGateway;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ILD.Api.Configuration;

/// <summary>
/// Serves the model gateway's HTTPS listener (ADR-0031) with the gateway's own certificate. Only the endpoint on
/// the gateway's port gets it; every other endpoint keeps whatever certificate it is configured with.
/// </summary>
internal static class GatewayTlsListener
{
    /// <summary>The URL to add to Kestrel's for the listener, bound to <paramref name="bindAddress"/>.</summary>
    public static string Url(GatewayTlsEndpoint tls, System.Net.IPAddress bindAddress)
        => $"https://{bindAddress}:{tls.Port}";

    /// <summary>
    /// The addresses Kestrel would have listened on by itself, which naming the gateway's listener replaces:
    /// <c>ASPNETCORE_URLS</c>, else every port of <c>ASPNETCORE_HTTP_PORTS</c>, else Kestrel's default.
    /// </summary>
    public static string WebUrls(string? urls, string? httpPorts)
    {
        if (!string.IsNullOrWhiteSpace(urls)) return urls;
        var ports = (httpPorts ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ports.Length > 0 ? string.Join(';', ports.Select(port => $"http://*:{port}")) : "http://localhost:5000";
    }

    public static void Configure(KestrelServerOptions kestrel, GatewayTlsEndpoint tls)
        => kestrel.ConfigureEndpointDefaults(listen =>
        {
            if (listen.IPEndPoint?.Port == tls.Port)
                listen.UseHttps(tls.Certificate);
        });
}
